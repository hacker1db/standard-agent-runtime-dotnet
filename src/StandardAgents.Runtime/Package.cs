using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StandardAgents.Runtime;

public sealed record ModelDef(string Name, string Provider, string Model, string? BaseUrl, string? ApiKeyEnv, List<string> Fallbacks);

public sealed record PromptToolRef(string Name, bool IncludeTextResponse, string? InitUserMessageProperty, Dictionary<string, string>? Env = null);

public sealed record PromptDef(string Name, string ToolDescription, string Model, string System,
    List<PromptToolRef> Tools, string ToolChoice, Dictionary<string, string> Env);

public sealed record ToolDef(string Name, string Description, JsonObject Args, List<string>? Command, string? Builtin);

public sealed record SideDef(string? Prompt, bool Human, bool StopOnResponse, string? StopTool, int MaxSteps);

public sealed record AgentDef(string Name, string Title, string? Description, SideDef SideA, SideDef SideB, bool Primary = true);

public sealed class PackageLoadException(string message) : Exception(message);

/// <summary>A loaded declarative package (FORMAT.md): registries of models, prompts, tools, agents.</summary>
public sealed class AgentPackage
{
    public required string Dir { get; init; }
    public required string Name { get; init; }
    public required List<string> EntryAgents { get; init; }
    public Dictionary<string, ModelDef> Models { get; } = new();
    public Dictionary<string, PromptDef> Prompts { get; } = new();
    public Dictionary<string, ToolDef> Tools { get; } = new();
    public Dictionary<string, AgentDef> Agents { get; } = new();

    public const int DefaultMaxSteps = 25, MaxStepsCap = 100;

    public static AgentPackage Load(string dir)
    {
        dir = Path.GetFullPath(dir);
        var pkgPath = Path.Combine(dir, "package.json");
        JsonObject pkg;
        try { pkg = File.Exists(pkgPath) ? JsonNode.Parse(File.ReadAllText(pkgPath)) as JsonObject ?? throw new PackageLoadException("package.json must be a JSON object") : new JsonObject(); }
        catch (JsonException e) { throw new PackageLoadException($"invalid package.json: {e.Message}"); }
        var defsPath = Path.Combine(dir, "agents.json");
        if (!File.Exists(defsPath)) throw new PackageLoadException($"missing {defsPath}");
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(defsPath)) as JsonObject ?? throw new PackageLoadException("agents.json must be a JSON object");
            return FromJson(dir, pkg, root);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException)
        {
            throw new PackageLoadException($"invalid agents.json: {e.Message}");
        }
    }

    /// <summary>Client API §5.6: prompt sideA + human sideB, and an entry agent when the package lists any.</summary>
    public bool IsCreatable(AgentDef a) =>
        !a.SideA.Human && a.SideA.Prompt is not null && a.SideB.Human && (EntryAgents.Count == 0 || EntryAgents.Contains(a.Name));

    public static AgentPackage FromJson(string dir, JsonObject pkg, JsonObject root)
    {
        var p = new AgentPackage
        {
            Dir = dir,
            Name = Str(pkg, "name") ?? Path.GetFileName(dir),
            EntryAgents = pkg["standardagent"]?["entryAgents"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [],
        };
        foreach (var (k, v) in Obj(root, "models"))
            p.Models[k] = new ModelDef(Str(v, "name") ?? k, Str(v, "provider") ?? "openai", Str(v, "model") ?? k,
                Str(v, "baseUrl"), Str(v, "apiKeyEnv"), StrList(v, "fallbacks"));
        foreach (var (k, v) in Obj(root, "tools"))
            p.Tools[k] = new ToolDef(k, Str(v, "description") ?? "", v["args"]?.DeepClone().AsObject() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                v["command"] is JsonArray c ? c.Select(n => n!.GetValue<string>()).ToList() : null, Str(v, "builtin"));

        var rawPrompts = Obj(root, "prompts").ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in rawPrompts)
        {
            var tools = (v["tools"] as JsonArray ?? []).Select(t => t is JsonValue
                ? new PromptToolRef(t.GetValue<string>(), true, null)
                : new PromptToolRef(Str(t!, "name") ?? throw new PackageLoadException($"prompt '{k}' has a tool reference without a name"),
                    t!["includeTextResponse"]?.GetValue<bool>() ?? true, Str(t, "initUserMessageProperty"),
                    (t["env"] as JsonObject)?.ToDictionary(e => e.Key, e => e.Value?.ToString() ?? ""))).ToList();
            var env = (v["env"] as JsonObject ?? []).ToDictionary(e => e.Key, e => e.Value?.ToString() ?? "");
            p.Prompts[k] = new PromptDef(Str(v, "name") ?? k, Str(v, "toolDescription") ?? "", Str(v, "model") ?? "",
                Resolve(k, rawPrompts, []), tools, Str(v, "toolChoice") ?? "auto", env);
        }
        foreach (var (k, v) in Obj(root, "agents"))
            p.Agents[k] = new AgentDef(Str(v, "name") ?? k, Str(v, "title") ?? Str(v, "name") ?? k, Str(v, "description"),
                Side(v["sideA"]), Side(v["sideB"]), v["primary"]?.GetValue<bool>() ?? true);
        p.Validate();
        return p;
    }

    /// <summary>Prompts §2.4: flatten text/include parts recursively, rejecting cycles.</summary>
    static string Resolve(string name, Dictionary<string, JsonNode> raw, List<string> stack)
    {
        if (stack.Contains(name)) throw new PackageLoadException($"circular prompt include: {string.Join(" -> ", stack)} -> {name}");
        if (!raw.TryGetValue(name, out var node)) throw new PackageLoadException($"unknown included prompt '{name}'");
        var prompt = node["prompt"];
        if (prompt is null) return "";
        if (prompt is JsonValue) return prompt.GetValue<string>();
        stack.Add(name);
        var text = string.Concat(prompt.AsArray().Select(part => Str(part!, "type") switch
        {
            "include" => Resolve(Str(part!, "prompt")!, raw, stack),
            _ => Str(part!, "content") ?? "",
        }));
        stack.RemoveAt(stack.Count - 1);
        return text;
    }

    void Validate()
    {
        foreach (var pr in Prompts.Values)
        {
            if (!Models.ContainsKey(pr.Model)) throw new PackageLoadException($"prompt '{pr.Name}' references unknown model '{pr.Model}'");
            foreach (var t in pr.Tools)
                if (!Tools.ContainsKey(t.Name) && !Prompts.ContainsKey(t.Name))
                    throw new PackageLoadException($"prompt '{pr.Name}' references unknown tool '{t.Name}'");
        }
        foreach (var m in Models.Values)
            if (m.ApiKeyEnv == "AGENT_RUNTIME_TOKEN") throw new PackageLoadException($"model '{m.Name}' may not use AGENT_RUNTIME_TOKEN as apiKeyEnv");
        foreach (var m in Models.Values)
            foreach (var f in m.Fallbacks)
                if (!Models.ContainsKey(f)) throw new PackageLoadException($"model '{m.Name}' has unknown fallback '{f}'");
        foreach (var t in Tools.Values)
            if (t.Command is not { Count: > 0 } && t.Builtin is not ("kv_set" or "kv_get"))
                throw new PackageLoadException($"tool '{t.Name}' needs a command or builtin kv_set/kv_get");
        foreach (var a in Agents.Values)
            foreach (var s in new[] { a.SideA, a.SideB })
                if (!s.Human && (s.Prompt is null || !Prompts.ContainsKey(s.Prompt)))
                    throw new PackageLoadException($"agent '{a.Name}' references unknown prompt '{s.Prompt}'");
    }

    static SideDef Side(JsonNode? n) => n is null ? new SideDef(null, true, true, null, DefaultMaxSteps) : new SideDef(
        Str(n, "prompt"), n["human"]?.GetValue<bool>() ?? Str(n, "prompt") is null,
        n["stopOnResponse"]?.GetValue<bool>() ?? true, Str(n, "stopTool"),
        Math.Clamp(n["maxSteps"]?.GetValue<int>() ?? DefaultMaxSteps, 1, MaxStepsCap));

    static IEnumerable<KeyValuePair<string, JsonNode>> Obj(JsonObject root, string key) =>
        (root[key] as JsonObject ?? []).Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value!));
    static string? Str(JsonNode n, string key) => n[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static List<string> StrList(JsonNode n, string key) => (n[key] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList();

    static readonly Regex Var = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Runtime §4: {{KEY}} from thread env, then prompt env defaults; unknown keys stay verbatim.</summary>
    public static string Interpolate(string text, IReadOnlyDictionary<string, string> threadEnv, IReadOnlyDictionary<string, string> defaults) =>
        Var.Replace(text, m => threadEnv.TryGetValue(m.Groups[1].Value, out var v) ? v
            : defaults.TryGetValue(m.Groups[1].Value, out var d) ? d : m.Value);
}
