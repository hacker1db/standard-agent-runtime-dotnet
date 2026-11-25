using System.Text.Json.Nodes;
using StandardAgents.Runtime;

namespace StandardAgents.IntegrationTests;

/// <summary>Full step loop over a subscription provider, with the CLI replaced by <see cref="FakeCli"/>.</summary>
[Trait("Category", "Integration")]
public sealed class SubscriptionRuntimeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sa-sub-").FullName;
    readonly FakeCli _cli = new();

    public void Dispose()
    {
        _cli.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    static JsonObject Defs(string provider) => JsonNode.Parse($$"""
        {
          "models": { "sub": { "name": "sub", "provider": "{{provider}}", "model": "default" } },
          "prompts": { "main": { "name": "main", "model": "sub", "prompt": "you are a test", "tools": ["remember", "recall"] } },
          "tools": {
            "remember": { "description": "store", "builtin": "kv_set" },
            "recall": { "description": "load", "builtin": "kv_get" }
          },
          "agents": { "bot": { "name": "bot", "sideA": { "prompt": "main" }, "sideB": { "human": true } } }
        }
        """)!.AsObject();

    [Theory]
    [InlineData("claude-cli")]
    [InlineData("codex-cli")]
    public async Task ToolRoundTripWithoutAnyApiKey(string provider)
    {
        string Wrap(string s) => provider == "claude-cli" ? FakeCli.ClaudeJson(s) : s;
        _cli.Reply(Wrap("""{"tool_calls":[{"name":"remember","arguments":{"key":"oncall","value":"Sam"}}]}"""), 1)
            .Reply(Wrap("""{"content":"Saved."}"""), 2);
        var providers = AgentRuntime.DefaultProviders();
        providers[provider] = _cli.Provider(provider);
        var rt = new AgentRuntime(AgentPackage.FromJson(_dir, new JsonObject(), Defs(provider)), new Store(Path.Combine(_dir, "t.db")), providers: providers);

        var t = rt.CreateThread("bot");
        rt.Enqueue(t.Id, "user", "remember Sam is on call");
        await rt.RunAsync(t.Id);

        Assert.Equal(2, _cli.Calls);
        var msgs = rt.Store.Messages(t.Id, maxDepth: 5);
        Assert.Contains(msgs, m => m.Role == "tool" && m.Name == "remember" && m.ToolStatus == "success");
        Assert.Equal("Saved.", msgs.Last(m => m.Role == "assistant").Content);
        Assert.Contains("[tool result id=", _cli.Stdin(2));
    }
}
