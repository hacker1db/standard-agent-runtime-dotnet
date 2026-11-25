using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using StandardAgents.Cli;
using StandardAgents.Runtime;

namespace StandardAgents.UnitTests;

public sealed class RuntimeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sa-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string DemoPackage()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "examples", "demo-agent"))) return Path.Combine(d.FullName, "examples", "demo-agent");
        throw new DirectoryNotFoundException("examples/demo-agent");
    }

    static JsonObject Defs(string extraPrompts = "", string side = """{"prompt":"main"}""") => JsonNode.Parse($$"""
        {
          "models": { "mock": { "name": "mock", "provider": "mock", "model": "mock" } },
          "prompts": { "main": { "name": "main", "model": "mock", "prompt": "hi", "tools": ["broken", "remember", "recall"] } {{extraPrompts}} },
          "tools": {
            "broken": { "description": "missing binary", "command": ["/nonexistent/tool-binary"] },
            "remember": { "description": "", "builtin": "kv_set" },
            "recall": { "description": "", "builtin": "kv_get" }
          },
          "agents": { "bot": { "name": "bot", "sideA": {{side}}, "sideB": { "human": true } } }
        }
        """)!.AsObject();

    AgentRuntime Runtime(AgentPackage pkg) => new(pkg, new Store(Path.Combine(_dir, $"{Guid.NewGuid():N}.db")));

    static async Task<List<MessageRow>> Send(AgentRuntime rt, string agent, string text)
    {
        var t = rt.CreateThread(agent);
        rt.Enqueue(t.Id, "user", text);
        await rt.RunAsync(t.Id);
        return rt.Store.Messages(t.Id, maxDepth: 5);
    }

    [Fact]
    public void CircularIncludeIsRejected()
    {
        var defs = Defs("""
            , "a": { "name": "a", "model": "mock", "prompt": [ { "type": "include", "prompt": "b" } ] }
            , "b": { "name": "b", "model": "mock", "prompt": [ { "type": "text", "content": "x" }, { "type": "include", "prompt": "a" } ] }
            """);
        var e = Assert.Throws<PackageLoadException>(() => AgentPackage.FromJson(_dir, new JsonObject(), defs));
        Assert.Contains("circular", e.Message);
    }

    [Fact]
    public void IncludesAndInterpolation()
    {
        var pkg = AgentPackage.Load(DemoPackage());
        var prompt = pkg.Prompts["ops_main"];
        Assert.Contains("Always use a tool", prompt.System);
        var text = AgentPackage.Interpolate(prompt.System, new Dictionary<string, string> { ["TEAM"] = "SRE" }, prompt.Env);
        Assert.StartsWith("You are Ferris, a helpful ops assistant for SRE.", text);
        Assert.Equal("{{MISSING}}", AgentPackage.Interpolate("{{MISSING}}", new Dictionary<string, string>(), prompt.Env));
    }

    [Fact]
    public async Task MockToolCallRunsCalculatorThenEchoes()
    {
        var rt = Runtime(AgentPackage.Load(DemoPackage()));
        var msgs = await Send(rt, "echo_bot", """/tool calculator {"expression":"1+2"}""");
        Assert.Equal(["user", "assistant", "tool", "assistant"], msgs.Select(m => m.Role));
        Assert.Contains("calculator", msgs[1].ToolCalls);
        Assert.Equal(("success", "3"), (msgs[2].ToolStatus, msgs[2].Content));
        Assert.StartsWith("echo: /tool calculator", msgs[3].Content);
    }

    [Fact]
    public async Task ToolErrorIsPersistedNotThrown()
    {
        var rt = Runtime(AgentPackage.FromJson(_dir, new JsonObject(), Defs()));
        var msgs = await Send(rt, "bot", "/tool broken {}");
        var tool = Assert.Single(msgs, m => m.Role == "tool");
        Assert.Equal("error", tool.ToolStatus);
        Assert.Equal("assistant", msgs[^1].Role);
    }

    [Fact]
    public async Task MaxStepsStopsTheLoop()
    {
        var pkg = AgentPackage.FromJson(_dir, new JsonObject(), Defs(side: """{"prompt":"main","stopOnResponse":false,"maxSteps":3}"""));
        var msgs = await Send(Runtime(pkg), "bot", "hello");
        Assert.Equal(3, msgs.Count(m => m.Role == "assistant"));
    }

    [Fact]
    public async Task KvRoundTripAndNullDelete()
    {
        var rt = Runtime(AgentPackage.FromJson(_dir, new JsonObject(), Defs()));
        var t = rt.CreateThread("bot");
        foreach (var cmd in new[] { """/tool remember {"key":"k","value":"v1"}""", """/tool recall {"key":"k"}""",
                                    """/tool remember {"key":"k","value":null}""", """/tool recall {"key":"k"}""" })
        {
            rt.Enqueue(t.Id, "user", cmd);
            await rt.RunAsync(t.Id);
        }
        var results = rt.Store.Messages(t.Id).Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Equal(["stored k", "v1", "deleted k", "null"], results);
        Assert.Null(rt.Store.GetValue(t.Id, "k"));
        rt.Store.SetValue(t.Id, "x", "1");
        Assert.Equal("1", rt.Store.GetValue(t.Id, "x"));
    }

    [Fact]
    public async Task HttpAuthSendAndTerminatedIs423()
    {
        var rt = Runtime(AgentPackage.Load(DemoPackage()));
        await using var app = Api.Build(rt, "secret", b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/agents")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        var created = await (await client.PostAsJsonAsync("/api/threads", new { agent_id = "echo_bot" })).Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["threadId"]!.ToString();

        var send = await client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "hello" });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        Assert.Equal("queued", (await send.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.ToString());

        JsonArray? messages = null;
        for (var i = 0; i < 50 && messages?.Count != 2; i++)
        {
            await Task.Delay(50);
            messages = (await client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}/messages"))!["messages"]!.AsArray();
        }
        Assert.Equal("echo: hello", messages![1]!["content"]!.ToString());

        rt.Terminate(id);
        var locked = await client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "again" });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }
}
