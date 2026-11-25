using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StandardAgents.Runtime;

public sealed class ThreadTerminatedException(string id) : Exception($"thread {id} is terminated");

/// <summary>Executes agents from a package against durable SQLite state (Runtime §3–§14 subset).</summary>
public sealed class AgentRuntime(AgentPackage package, Store store, HttpClient? http = null, IReadOnlyDictionary<string, IProvider>? providers = null)
{
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);
    public const int MaxToolOutputBytes = 1 << 20;
    public const int MaxPromptToolDepth = 3;
    static readonly string[] ToolEnvAllowlist = ["PATH", "HOME", "LANG", "TMPDIR"];

    /// <summary>Per-thread coordination: at most one runner task; the in-flight run's cancellation source.</summary>
    sealed class Ctl
    {
        public Task? Runner;
        public CancellationTokenSource? Cts;
    }

    readonly ConcurrentDictionary<string, Ctl> _ctl = new();
    readonly IReadOnlyDictionary<string, IProvider> _providers = providers ?? DefaultProviders(http);

    /// <summary>The built-in providers. None of them needs an API key: <c>openai</c> sends one only when its env var is set.</summary>
    public static Dictionary<string, IProvider> DefaultProviders(HttpClient? http = null) => new()
    {
        ["mock"] = new MockProvider(),
        ["openai"] = new OpenAiProvider(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }),
        ["claude-cli"] = new CliProvider("claude-cli"),
        ["codex-cli"] = new CliProvider("codex-cli"),
    };

    public AgentPackage Package => package;
    public Store Store => store;

    /// <summary>Raised for every created or updated message, including queued ("pending") ones.</summary>
    public event Action<MessageRow>? MessageAdded;
    /// <summary>Raised on every run status change.</summary>
    public event Action<string, ThreadStatus>? StatusChanged;
    /// <summary>Raised once a thread's rows are deleted.</summary>
    public event Action<string>? ThreadDeleted;

    public ThreadStatus Status(string threadId) => store.GetStatus(threadId);

    public ThreadRow CreateThread(string agentId, List<string>? tags = null, Dictionary<string, string>? env = null) =>
        package.Agents.ContainsKey(agentId) ? store.CreateThread(agentId, tags ?? [], env ?? [])
            : throw new KeyNotFoundException($"unknown agent '{agentId}'");

    /// <summary>Durably queues a message (Runtime §9) atomically with the terminated check. Does not start a run.</summary>
    public string Enqueue(string threadId, string role, string content)
    {
        var (result, pending) = store.Enqueue(threadId, role, content);
        return result switch
        {
            EnqueueResult.NotFound => throw new KeyNotFoundException($"unknown thread '{threadId}'"),
            EnqueueResult.Terminated => throw new ThreadTerminatedException(threadId),
            _ => Emit(pending!).Id,
        };
    }

    MessageRow Emit(MessageRow m) { MessageAdded?.Invoke(m); return m; }

    void SetStatus(string threadId, string state, string? reason)
    {
        if (store.SetStatus(threadId, state, reason) is { } s) StatusChanged?.Invoke(threadId, s);
    }

    /// <summary>Client API §15: abort the in-flight run, if any. An idle thread is not changed.</summary>
    public void Stop(string threadId)
    {
        if (!_ctl.TryGetValue(threadId, out var ctl)) return;
        lock (ctl) ctl.Cts?.Cancel();
    }

    /// <summary>Runtime §10: terminate the thread and abort any in-flight execution.</summary>
    public void Terminate(string threadId)
    {
        store.Terminate(threadId);
        Stop(threadId);
    }

    /// <summary>Client API §10: terminate, abort and await the in-flight run, then delete rows.</summary>
    public async Task<bool> DeleteAsync(string threadId)
    {
        if (store.GetThread(threadId) is null) return false;
        Terminate(threadId);
        if (_ctl.TryGetValue(threadId, out var ctl))
        {
            Task? runner;
            lock (ctl) runner = ctl.Runner;
            if (runner is not null) await runner;
        }
        var deleted = store.DeleteThread(threadId);
        _ctl.TryRemove(threadId, out _);
        if (deleted) ThreadDeleted?.Invoke(threadId);
        return deleted;
    }

    /// <summary>Startup recovery (Execution State §4.3): lost runs become failed/execution_lost; queued threads resume.</summary>
    public void Recover()
    {
        foreach (var id in store.RunningThreads()) SetStatus(id, "failed", "execution_lost");
        foreach (var id in store.ThreadsWithQueue()) _ = RunAsync(id);
    }

    /// <summary>
    /// Ensures a runner drains the thread's queue and returns it. Runtime §11: one execution per thread; concurrent
    /// callers coalesce onto the same runner. Never throws; failures settle the run status as failed.
    /// </summary>
    public Task RunAsync(string threadId)
    {
        var ctl = _ctl.GetOrAdd(threadId, _ => new Ctl());
        lock (ctl) return ctl.Runner ??= Task.Run(() => RunnerAsync(threadId, ctl));
    }

    async Task RunnerAsync(string threadId, Ctl ctl)
    {
        while (true)
        {
            CancellationTokenSource cts;
            lock (ctl)
            {
                // The exit check shares the lock with RunAsync, so a message queued before RunAsync is never stranded.
                if (!store.HasQueued(threadId)) { ctl.Runner = null; return; }
                ctl.Cts = cts = new CancellationTokenSource();
            }
            bool more;
            try { more = await RunOnceAsync(threadId, cts.Token); }
            catch (Exception) { more = false; }
            finally
            {
                lock (ctl) ctl.Cts = null;
                cts.Dispose();
            }
            if (!more) { lock (ctl) ctl.Runner = null; return; }
        }
    }

    /// <summary>One execution. Returns false when the runner must stop (thread gone/terminated or unrecoverable).</summary>
    async Task<bool> RunOnceAsync(string threadId, CancellationToken ct)
    {
        var thread = store.GetThread(threadId);
        if (thread is null || thread.Terminated is not null) return false;
        SetStatus(threadId, "running", null);
        try
        {
            var agent = package.Agents.GetValueOrDefault(thread.AgentId)
                        ?? throw new InvalidOperationException($"agent '{thread.AgentId}' is no longer defined");
            if (agent.SideA.Human || agent.SideA.Prompt is null || !package.Prompts.TryGetValue(agent.SideA.Prompt, out var prompt))
                throw new InvalidOperationException($"agent '{agent.Name}' side A is not prompt-backed");
            var (_, reason) = await StepLoopAsync(thread, prompt, agent.SideA, depth: 0, local: null, env: thread.Env, ct);
            if (reason == "max_side_steps") StatusMessage(threadId, reason, $"side A reached maxSteps ({agent.SideA.MaxSteps})");
            SetStatus(threadId, "idle", reason);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(threadId, "idle", "aborted_by_user");
            return true;
        }
        catch (Exception e)
        {
            if (store.GetThread(threadId) is not null)
            {
                StatusMessage(threadId, "step_error", e.Message);
                SetStatus(threadId, "failed", "step_error");
            }
            store.DrainQueue(threadId, "b").ForEach(m => Emit(m)); // never re-run failed input in a hot loop
            return false;
        }
    }

    /// <summary>Runtime status message: system, silent, metadata.status_kind; never sent to the model.</summary>
    void StatusMessage(string threadId, string kind, string text) =>
        Emit(store.AddMessage(threadId, "system", text, "a", silent: true, statusKind: kind));

    /// <summary>Runtime §3 step cycle; returns the last assistant text and the stop reason. local != null means a nested prompt-tool loop.</summary>
    async Task<(string? Text, string Reason)> StepLoopAsync(ThreadRow thread, PromptDef prompt, SideDef side, int depth,
        List<ChatMessage>? local, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var system = AgentPackage.Interpolate(prompt.System, env, prompt.Env);
        var tools = ToolSpecs(prompt);
        string? lastText = null;
        for (var step = 0; step < side.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (local is null) store.DrainQueue(thread.Id, "b").ForEach(m => Emit(m));
            var history = local ?? Context(store.Messages(thread.Id));
            var response = await CompleteAsync(prompt.Model, [new ChatMessage("system", system), .. history], tools, prompt.ToolChoice, ct);
            ct.ThrowIfCancellationRequested();
            lastText = response.Text;
            Persist(thread.Id, new ChatMessage("assistant", response.Text, response.ToolCalls), depth, local, prompt.Name);

            var stop = false;
            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, result) = await ExecuteToolAsync(thread, prompt, call, depth, env, ct);
                ct.ThrowIfCancellationRequested();
                Persist(thread.Id, new ChatMessage("tool", result, ToolCallId: call.Id, Name: call.Name), depth, local, prompt.Name, ok ? "success" : "error");
                stop |= side.StopTool is not null && call.Name == side.StopTool;
            }
            if (stop) return (lastText, "stop_tool");                                                // §6: stopTool
            if (response.ToolCalls.Count == 0 && side.StopOnResponse) return (lastText, "stop_on_response"); // §6: stopOnResponse
        }
        return (lastText, "max_side_steps");                                                         // §6: maxSteps
    }

    void Persist(string threadId, ChatMessage m, int depth, List<ChatMessage>? local, string prompt, string? toolStatus = null)
    {
        local?.Add(m);
        var calls = m.ToolCalls is { Count: > 0 } ? OpenAiProvider.ToolCallsJson(m.ToolCalls).ToJsonString() : null;
        Emit(store.AddMessage(threadId, m.Role, m.Content, "a", depth, m.Name, calls, m.ToolCallId, toolStatus, prompt: prompt));
    }

    /// <summary>
    /// Model context: drops runtime status messages and inserts a synthetic error result for every assistant tool
    /// call left unanswered (crash or abort), so providers always see matched call/result pairs.
    /// </summary>
    public static List<ChatMessage> Context(IEnumerable<MessageRow> rows)
    {
        var list = rows.Where(r => r.StatusKind is null).ToList();
        var answered = list.Where(r => r.Role == "tool" && r.ToolCallId is not null).Select(r => r.ToolCallId!).ToHashSet();
        var result = new List<ChatMessage>();
        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var calls = r.ToolCalls is null ? null : OpenAiProvider.ParseToolCalls(r.ToolCalls);
            result.Add(new ChatMessage(r.Role, r.Content, calls, r.ToolCallId, r.Name));
            if (calls is not { Count: > 0 }) continue;
            while (i + 1 < list.Count && list[i + 1].Role == "tool")
            {
                var t = list[++i];
                result.Add(new ChatMessage(t.Role, t.Content, null, t.ToolCallId, t.Name));
            }
            foreach (var c in calls.Where(c => !answered.Contains(c.Id)))
                result.Add(new ChatMessage("tool", "error: tool call did not complete (execution was interrupted)", null, c.Id, c.Name));
        }
        return result;
    }

    /// <summary>Models §3: try the model, then each fallback in order, on provider error.</summary>
    async Task<ModelResponse> CompleteAsync(string modelName, List<ChatMessage> messages, List<ToolSpec> tools, string toolChoice, CancellationToken ct)
    {
        var primary = package.Models[modelName];
        var errors = new List<string>();
        foreach (var name in new[] { modelName }.Concat(primary.Fallbacks).Distinct())
        {
            var model = package.Models[name];
            try
            {
                var provider = _providers.GetValueOrDefault(model.Provider) ?? throw new NotSupportedException($"unknown provider '{model.Provider}'");
                return await provider.CompleteAsync(new ModelRequest(model, messages, tools, toolChoice), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{name}: {e.Message}");
            }
        }
        throw new InvalidOperationException("all models failed: " + string.Join("; ", errors));
    }

    List<ToolSpec> ToolSpecs(PromptDef prompt) => prompt.Tools.Select(t => package.Tools.TryGetValue(t.Name, out var def)
        ? new ToolSpec(t.Name, def.Description, def.Args)
        : new ToolSpec(t.Name, package.Prompts[t.Name].ToolDescription, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [t.InitUserMessageProperty ?? "input"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray(t.InitUserMessageProperty ?? "input"),
        })).ToList();

    /// <summary>Env precedence: prompt.env &lt; tool-ref env &lt; thread env.</summary>
    static Dictionary<string, string> MergeEnv(params IReadOnlyDictionary<string, string>?[] layers)
    {
        var d = new Dictionary<string, string>();
        foreach (var l in layers)
            if (l is not null) foreach (var (k, v) in l) d[k] = v;
        return d;
    }

    /// <summary>Runtime §5.2: tool failures become error results; they never escape the loop.</summary>
    async Task<(bool Ok, string Result)> ExecuteToolAsync(ThreadRow thread, PromptDef prompt, ToolCall call, int depth,
        IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        try
        {
            var toolRef = prompt.Tools.FirstOrDefault(t => t.Name == call.Name);
            if (toolRef is null) return (false, $"tool '{call.Name}' is not available to prompt '{prompt.Name}'");
            var args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject
                       ?? throw new ArgumentException("tool arguments must be a JSON object");

            if (package.Prompts.TryGetValue(call.Name, out var sub) && !package.Tools.ContainsKey(call.Name))
            {
                if (depth + 1 > MaxPromptToolDepth) return (false, $"prompt tool '{call.Name}' exceeds the maximum nesting depth of {MaxPromptToolDepth}");
                var input = args[toolRef.InitUserMessageProperty ?? "input"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : args.ToJsonString();
                var side = new SideDef(sub.Name, false, true, null, AgentPackage.DefaultMaxSteps);
                var subEnv = MergeEnv(toolRef.Env, thread.Env);
                var (text, _) = await StepLoopAsync(thread, sub, side, depth + 1, [new ChatMessage("user", input)], subEnv, ct);
                return (true, toolRef.IncludeTextResponse ? text ?? "" : "");
            }

            var def = package.Tools[call.Name];
            return def.Builtin switch
            {
                "kv_set" => KvSet(thread.Id, args),
                "kv_get" => (true, store.GetValue(thread.Id, Required(args, "key")) ?? "null"),
                _ => await RunCommandAsync(def, thread, args, MergeEnv(prompt.Env, toolRef.Env, env), ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return (false, e.Message); }
    }

    (bool, string) KvSet(string threadId, JsonObject args)
    {
        var key = Required(args, "key");
        var value = args["value"] is null ? null : args["value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : args["value"]!.ToJsonString();
        store.SetValue(threadId, key, value);
        return (true, value is null ? $"deleted {key}" : $"stored {key}");
    }

    static string Required(JsonObject args, string key) =>
        args[key]?.ToString() is { Length: > 0 } s ? s : throw new ArgumentException($"missing required argument '{key}'");

    static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Reads up to <paramref name="cap"/> bytes; returns null when the stream exceeds it.</summary>
    static async Task<string?> ReadCappedAsync(Stream s, int cap, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(chunk, ct)) > 0)
        {
            if (buf.Length + n > cap) return null;
            buf.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
    }

    async Task<(bool, string)> RunCommandAsync(ToolDef def, ThreadRow thread, JsonObject args, Dictionary<string, string> env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(def.Command![0])
        {
            WorkingDirectory = package.Dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in def.Command.Skip(1)) psi.ArgumentList.Add(a);
        psi.Environment.Clear(); // never leak host secrets (AGENT_RUNTIME_TOKEN, API keys) to tools
        foreach (var k in ToolEnvAllowlist)
            if (Environment.GetEnvironmentVariable(k) is { } v) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {def.Command[0]}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            // Readers start before stdin is written, all inside the timeout, so a chatty tool cannot deadlock us.
            var stdout = ReadCappedAsync(proc.StandardOutput.BaseStream, MaxToolOutputBytes, timeout.Token);
            var stderr = ReadCappedAsync(proc.StandardError.BaseStream, 64 * 1024, timeout.Token);
            var payload = new JsonObject
            {
                ["args"] = args.DeepClone(),
                ["env"] = JsonSerializer.SerializeToNode(env),
                ["threadId"] = thread.Id,
            };
            try
            {
                await proc.StandardInput.WriteAsync(payload.ToJsonString().AsMemory(), timeout.Token);
                proc.StandardInput.Close();
            }
            catch (IOException) { } // tool exited without reading stdin
            var output = await stdout;
            if (output is null)
            {
                KillTree(proc);
                return (false, $"tool '{def.Name}' output exceeds {MaxToolOutputBytes} bytes");
            }
            await proc.WaitForExitAsync(timeout.Token);
            if (proc.ExitCode != 0) return (false, $"tool '{def.Name}' exited with code {proc.ExitCode}: {((await stderr) ?? "").Trim()}");
            JsonNode? result;
            try { result = JsonNode.Parse(output); }
            catch (JsonException) { return (false, $"tool '{def.Name}' returned invalid JSON"); }
            return result?["status"]?.ToString() switch
            {
                "success" => (true, Stringify(result!["result"])),
                "error" => (false, Stringify(result!["error"])),
                _ => (false, $"tool '{def.Name}' returned no status"),
            };
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            if (ct.IsCancellationRequested) throw;
            return (false, $"tool '{def.Name}' timed out after {ToolTimeout.TotalSeconds:0}s");
        }
    }

    static string Stringify(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
}
