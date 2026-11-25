using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StandardAgents.Runtime;

/// <summary>
/// Subscription-backed providers (extension, not in the spec): drive the official, locally
/// logged-in CLIs headlessly. <c>claude-cli</c> runs <c>claude -p</c>; <c>codex-cli</c> runs
/// <c>codex exec</c>. API-key env vars are removed so the CLI bills the subscription. The CLIs'
/// own tools are disabled / sandboxed read-only; this runtime executes tools via a JSON reply
/// protocol embedded in the prompt. No tokens are read or replayed.
/// </summary>
public sealed class CliProvider : IProvider
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);
    static readonly string[] RemovedEnv = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"];

    readonly string _kind;
    readonly string _bin;
    readonly TimeSpan _timeout;

    /// <param name="kind"><c>claude-cli</c> or <c>codex-cli</c>.</param>
    /// <param name="bin">CLI executable. Defaults to <c>SA_CLAUDE_BIN</c> / <c>SA_CODEX_BIN</c>, then <c>claude</c> / <c>codex</c> on PATH.</param>
    /// <param name="timeout">Per-call timeout. Defaults to <see cref="DefaultTimeout"/>.</param>
    public CliProvider(string kind, string? bin = null, TimeSpan? timeout = null)
    {
        var (envVar, fallback) = kind switch
        {
            "claude-cli" => ("SA_CLAUDE_BIN", "claude"),
            "codex-cli" => ("SA_CODEX_BIN", "codex"),
            _ => throw new NotSupportedException($"unknown cli provider {kind}"),
        };
        _kind = kind;
        _bin = bin ?? (Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } v ? v : fallback);
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Provider names that run a local, logged-in subscription CLI.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "claude-cli", "codex-cli" };

    public async Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct)
    {
        var (system, transcript) = Render(req);
        var text = _kind == "claude-cli"
            ? await ClaudeAsync(req.Model, system, transcript, ct)
            : await CodexAsync(req.Model, $"{system}\n\n{transcript}", ct);
        return ParseReply(text, req.Tools.Count > 0);
    }

    /// <summary>System prompt (with tool protocol) and a plain-text transcript.</summary>
    public static (string System, string Transcript) Render(ModelRequest req)
    {
        var system = new StringBuilder();
        var transcript = new StringBuilder();
        foreach (var m in req.Messages)
        {
            var content = m.Content ?? "";
            switch (m.Role)
            {
                case "system": system.Append(content); break;
                case "user": transcript.Append(CultureInfo.InvariantCulture, $"[user]\n{content}\n\n"); break;
                case "assistant":
                    if (content.Length > 0) transcript.Append(CultureInfo.InvariantCulture, $"[assistant]\n{content}\n\n");
                    foreach (var tc in m.ToolCalls ?? [])
                        transcript.Append(CultureInfo.InvariantCulture, $"[assistant tool call id={tc.Id}]\n{tc.Name} {(string.IsNullOrEmpty(tc.Arguments) ? "{}" : tc.Arguments)}\n\n");
                    break;
                case "tool": transcript.Append(CultureInfo.InvariantCulture, $"[tool result id={m.ToolCallId ?? ""}]\n{content}\n\n"); break;
            }
        }
        if (req.Tools.Count > 0)
        {
            var catalog = new JsonArray(req.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
            }).ToArray());
            system.Append("\n\n# Tools\nYou can call these tools (JSON Schema parameters):\n")
                .Append(catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
                .Append("\n\n# Reply format (mandatory)\nReply with ONE JSON object and nothing else.\n")
                .Append("To call tools: {\"tool_calls\":[{\"name\":\"<tool>\",\"arguments\":{...}}]}\n")
                .Append("To answer the user: {\"content\":\"<your answer>\"}\n")
                .Append("Tool results arrive as [tool result] blocks in the transcript. Do not use any other tools.");
        }
        transcript.Append("[assistant]\n");
        return (system.ToString(), transcript.ToString());
    }

    /// <summary>Extracts the JSON reply; falls back to treating the text as a plain answer.</summary>
    public static ModelResponse ParseReply(string text, bool toolsEnabled)
    {
        var trimmed = text.Trim();
        if (toolsEnabled && FirstJsonObject(trimmed) is { } v)
        {
            if (v["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                var toolCalls = calls
                    .Where(c => c?["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String)
                    .Select(c => new ToolCall("call_" + Guid.NewGuid().ToString("N"), c!["name"]!.GetValue<string>(),
                        c["arguments"] is JsonObject a ? a.ToJsonString() : "{}"))
                    .ToList();
                if (toolCalls.Count > 0) return new ModelResponse(null, toolCalls);
            }
            if (v["content"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.String)
                return new ModelResponse(cv.GetValue<string>(), []);
        }
        return new ModelResponse(trimmed, []);
    }

    /// <summary>First balanced <c>{...}</c> that parses as a JSON object (tolerates fences / prose).</summary>
    public static JsonObject? FirstJsonObject(string s)
    {
        for (var start = s.IndexOf('{'); start >= 0; start = s.IndexOf('{', start + 1))
        {
            int depth = 0; bool inStr = false, esc = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try { if (JsonNode.Parse(s[start..(i + 1)]) is JsonObject o) return o; } catch (JsonException) { }
                    break;
                }
            }
        }
        return null;
    }

    ProcessStartInfo BaseStart()
    {
        var psi = new ProcessStartInfo(_bin)
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var k in RemovedEnv) psi.Environment.Remove(k);
        return psi;
    }

    async Task<string> RunAsync(ProcessStartInfo psi, string stdin, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start CLI"); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException($"failed to start CLI '{psi.FileName}' (is it installed and on PATH?): {e.Message}", e); }
        using (proc)
        {
            try
            {
                var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);
                proc.StandardInput.Close();
                await proc.WaitForExitAsync(timeout.Token);
                var (o, e) = (await stdout, await stderr);
                if (proc.ExitCode != 0) throw new InvalidOperationException($"CLI exited {proc.ExitCode}: {e.Trim()}");
                return o;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("CLI timed out");
            }
        }
    }

    async Task<string> ClaudeAsync(ModelDef model, string system, string transcript, CancellationToken ct)
    {
        var psi = BaseStart();
        foreach (var a in new[] { "-p", "--output-format", "json", "--tools", "", "--strict-mcp-config", "--no-session-persistence", "--setting-sources", "", "--system-prompt", system })
            psi.ArgumentList.Add(a);
        if (model.Model != "default") { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model.Model); }
        var output = await RunAsync(psi, transcript, ct);
        JsonNode? v;
        try { v = JsonNode.Parse(output.Trim()); } catch (JsonException e) { throw new InvalidOperationException("claude returned non-JSON", e); }
        if (v?["is_error"] is JsonValue err && err.GetValueKind() == JsonValueKind.True)
            throw new InvalidOperationException($"claude error: {v["result"]?.ToJsonString()}");
        return v?["result"] is JsonValue r && r.GetValueKind() == JsonValueKind.String ? r.GetValue<string>()
            : throw new InvalidOperationException("claude output missing `result`");
    }

    async Task<string> CodexAsync(ModelDef model, string prompt, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"sa-codex-{Guid.NewGuid():N}.txt");
        var psi = BaseStart();
        foreach (var a in new[] { "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never", "-o", outFile })
            psi.ArgumentList.Add(a);
        if (model.Model != "default") { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model.Model); }
        psi.ArgumentList.Add("-");
        try
        {
            await RunAsync(psi, prompt, ct);
            try { return await File.ReadAllTextAsync(outFile, ct); }
            catch (IOException e) { throw new InvalidOperationException("codex produced no final message", e); }
        }
        finally { try { File.Delete(outFile); } catch { } }
    }
}
