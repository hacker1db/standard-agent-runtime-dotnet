using StandardAgents.Runtime;

namespace StandardAgents.Tests;

/// <summary>
/// Opt-in checks against the real CLIs and your own subscription login. No API key is used.
/// <c>SA_LIVE_SUBSCRIPTION=1 dotnet test --filter Category=LiveSubscription</c>
/// </summary>
[Trait("Category", "LiveSubscription")]
public sealed class LiveSubscriptionTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sa-live-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string DemoPackage()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "examples", "demo-agent"))) return Path.Combine(d.FullName, "examples", "demo-agent");
        throw new DirectoryNotFoundException("examples/demo-agent");
    }

    async Task<string?> Ask(string agent, string text)
    {
        var rt = new AgentRuntime(AgentPackage.Load(DemoPackage()), new Store(Path.Combine(_dir, $"{agent}.db")));
        var t = rt.CreateThread(agent);
        rt.Enqueue(t.Id, "user", text);
        await rt.RunAsync(t.Id);
        Assert.NotEqual("failed", rt.Status(t.Id).State);
        return rt.Store.Messages(t.Id, maxDepth: 5).LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))?.Content;
    }

    [LiveSubscriptionFact("claude")]
    public async Task ClaudeSubscriptionUsesTheCalculator() =>
        Assert.Contains("391", await Ask("ops_helper_claude", "What is 17*23? Use the calculator.") ?? "");

    [LiveSubscriptionFact("codex")]
    public async Task CodexSubscriptionUsesTheCalculator() =>
        Assert.Contains("391", await Ask("ops_helper_codex", "What is 17*23? Use the calculator.") ?? "");
}
