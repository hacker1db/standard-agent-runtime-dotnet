using System.Net;
using StandardAgents.Cli;
using StandardAgents.Runtime;

const string Usage = """
    usage:
      agents serve --package DIR [--host 127.0.0.1] [--port 8788] [--db FILE] [--ui DIR] [--allow-remote]
      agents chat  --package DIR --agent NAME [--db FILE]
      agents run   --package DIR --agent NAME --message TEXT [--db FILE]
    """;

if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
var opts = new Dictionary<string, string>();
string[] flags = ["allow-remote"];
for (var i = 1; i < args.Length; i++)
{
    var key = args[i].TrimStart('-');
    if (flags.Contains(key)) { opts[key] = "true"; continue; }
    if (i + 1 < args.Length) opts[key] = args[++i];
}
string Opt(string k, string? d = null) => opts.GetValueOrDefault(k) ?? d ?? throw new ArgumentException($"--{k} is required\n{Usage}");

try
{
    var package = AgentPackage.Load(Opt("package"));
    var store = new Store(Opt("db", args[0] == "run" ? Path.Combine(Path.GetTempPath(), $"agents-{Guid.NewGuid():N}.db") : "agents.db"));
    var rt = new AgentRuntime(package, store);

    switch (args[0])
    {
        case "serve":
            var host = Opt("host", "127.0.0.1");
            var loopback = host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
            var envToken = Environment.GetEnvironmentVariable("AGENT_RUNTIME_TOKEN");
            if (envToken is { Length: 0 }) throw new ArgumentException("AGENT_RUNTIME_TOKEN is set but empty");
            var token = envToken ?? (loopback ? "dev-token" : throw new ArgumentException("AGENT_RUNTIME_TOKEN is required when not bound to loopback"));
            if (!loopback && !opts.ContainsKey("allow-remote") && package.Models.Values.FirstOrDefault(m => CliProvider.Kinds.Contains(m.Provider)) is { } cliModel)
                throw new ArgumentException($"model '{cliModel.Name}' uses the local {cliModel.Provider} subscription login; bind to loopback or pass --allow-remote");
            var ui = opts.GetValueOrDefault("ui");
            if (ui is not null && !Directory.Exists(ui)) throw new ArgumentException($"--ui directory not found: {ui}");
            var app = Api.Build(rt, token, uiDir: ui);
            rt.Recover();
            var url = $"http://{(host.Contains(':') ? $"[{host}]" : host)}:{Opt("port", "8788")}";
            Console.WriteLine($"serving package '{package.Name}' on {url}/api{(ui is null ? "" : $" (ui at {url}/)")}");
            await app.RunAsync(url);
            return 0;

        case "run":
        case "chat":
            var agent = Opt("agent");
            var thread = rt.CreateThread(agent);
            rt.MessageAdded += Print;
            if (args[0] == "run")
            {
                rt.Enqueue(thread.Id, "user", Opt("message"));
                await rt.RunAsync(thread.Id);
                return rt.Status(thread.Id).State == "failed" ? 1 : 0;
            }
            Console.WriteLine($"chatting with {agent} (thread {thread.Id}); empty line or Ctrl-D to exit");
            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return 0;
                rt.Enqueue(thread.Id, "user", line);
                await rt.RunAsync(thread.Id);
            }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or PackageLoadException or KeyNotFoundException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static void Print(MessageRow m)
{
    if (m.Status == "pending") return;
    var indent = new string(' ', m.Depth * 2);
    if (m.Role == "assistant" && m.ToolCalls is not null)
        foreach (var c in OpenAiProvider.ParseToolCalls(m.ToolCalls)) Console.WriteLine($"{indent}[tool call] {c.Name} {c.Arguments}");
    if (m.Role == "tool") Console.WriteLine($"{indent}[tool {m.ToolStatus}] {m.Name}: {m.Content}");
    else if (m.Role == "system" && m.StatusKind is not null) Console.Error.WriteLine($"{indent}[{m.StatusKind}] {m.Content}");
    else if (m.Role == "assistant" && !string.IsNullOrEmpty(m.Content)) Console.WriteLine($"{indent}{(m.Depth > 0 ? "[nested] " : "")}{m.Content}");
}
