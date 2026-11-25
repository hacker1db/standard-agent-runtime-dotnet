using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using StandardAgents.Cli;
using StandardAgents.Runtime;

namespace StandardAgents.Tests;

public sealed class HttpApiTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sa-http-").FullName;
    WebApplication _app = null!;
    HttpClient _client = null!;

    static string DemoPackage()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "examples", "demo-agent"))) return Path.Combine(d.FullName, "examples", "demo-agent");
        throw new DirectoryNotFoundException("examples/demo-agent");
    }

    public async Task InitializeAsync()
    {
        var rt = new AgentRuntime(AgentPackage.Load(DemoPackage()), new Store(Path.Combine(_dir, "t.db")));
        _app = Api.Build(rt, "secret", b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    async Task<string> NewThread() =>
        (await (await _client.PostAsJsonAsync("/api/threads", new { agent_id = "echo_bot" })).Content.ReadFromJsonAsync<JsonObject>())!["threadId"]!.ToString();

    async Task<string> Ticket(string path)
    {
        var r = await _client.PostAsJsonAsync("/api/auth/websocket-ticket", new { pathname = path });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var body = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(60, body["expires_in"]!.GetValue<int>());
        return body["ticket"]!.ToString();
    }

    async Task<HttpStatusCode> UpgradeStatus(string pathAndQuery)
    {
        var ws = _app.GetTestServer().CreateWebSocketClient();
        try { using var s = await ws.ConnectAsync(new Uri("ws://localhost" + pathAndQuery), CancellationToken.None); return HttpStatusCode.SwitchingProtocols; }
        catch (InvalidOperationException e) when (e.Message.Contains("401")) { return HttpStatusCode.Unauthorized; }
    }

    static async Task<JsonObject> Receive(WebSocket ws)
    {
        var buf = new byte[65536];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sb = new StringBuilder();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, cts.Token); sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); } while (!r.EndOfMessage);
        return JsonNode.Parse(sb.ToString())!.AsObject();
    }

    [Fact]
    public async Task CreateThreadWithEmptyBodyIs400()
    {
        var r = await _client.PostAsJsonAsync("/api/threads", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.NotNull((await r.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/threads", new { agent_id = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/threads", new { agent_id = "echo_bot", tags = new[] { 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/threads", new { agent_id = "nope" })).StatusCode);
    }

    [Fact]
    public async Task TicketIsSingleUseAndPathBound()
    {
        var id = await NewThread();
        var path = $"/api/threads/{id}/stream";
        var ticket = await Ticket(path);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, await UpgradeStatus($"{path}?ticket={ticket}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await UpgradeStatus($"{path}?ticket={ticket}")); // used

        var other = await NewThread();
        var mismatched = await Ticket(path);
        Assert.Equal(HttpStatusCode.Unauthorized, await UpgradeStatus($"/api/threads/{other}/stream?ticket={mismatched}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await UpgradeStatus($"{path}?ticket=bogus"));

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/auth/websocket-ticket", new { pathname = "/api/../x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/auth/websocket-ticket", new { pathname = "/other" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(path)).StatusCode); // non-upgrade
    }

    [Fact]
    public async Task StreamStartsWithStatusThenDeliversMessages()
    {
        var id = await NewThread();
        var path = $"/api/threads/{id}/stream";
        var ws = await _app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost{path}?ticket={await Ticket(path)}"), CancellationToken.None);
        var first = await Receive(ws);
        Assert.Equal("thread_status", first["type"]!.ToString());
        Assert.Equal("idle", first["status"]!["state"]!.ToString());

        await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "hello" });
        var frames = new List<JsonObject>();
        while (!frames.Any(f => f["type"]!.ToString() == "message_data" && f["data"]!["role"]!.ToString() == "assistant"))
            frames.Add(await Receive(ws));
        var reply = frames.Last()["data"]!;
        Assert.Equal("echo: hello", reply["content"]!.ToString());
        Assert.Equal("completed", reply["status"]!.ToString());
        Assert.Equal(frames.Last()["message_id"]!.ToString(), reply["id"]!.ToString());
        Assert.Contains(frames, f => f["type"]!.ToString() == "message_data" && f["data"]!["status"]!.ToString() == "pending");
        Assert.Contains(frames, f => f["type"]!.ToString() == "thread_status" && f["status"]!["state"]!.ToString() == "running");

        await ws.SendAsync(Encoding.UTF8.GetBytes("stream_ping"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[256];
        string text;
        do
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            text = Encoding.UTF8.GetString(buf, 0, r.Count);
        } while (text != "stream_pong");

        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"/api/threads/{id}")).StatusCode);
        var close = await ws.ReceiveAsync(buf, CancellationToken.None);
        while (close.MessageType != WebSocketMessageType.Close) close = await ws.ReceiveAsync(buf, CancellationToken.None);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
    }

    [Fact]
    public async Task StopOnIdleThreadLeavesItOpen()
    {
        var id = await NewThread();
        var before = (await _client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}"))!["status"]!.ToJsonString();
        var stop = await _client.PostAsync($"/api/threads/{id}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal("stopped", (await stop.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.ToString());
        var thread = (await _client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}"))!;
        Assert.Equal(before, thread["status"]!.ToJsonString());
        Assert.Null(thread["thread"]!["terminated"]);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "hi" })).StatusCode);
    }

    [Fact]
    public async Task DeleteThenEverythingIs404()
    {
        var id = await NewThread();
        await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "hi" });
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"/api/threads/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/threads/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/threads/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/threads/{id}/stream")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/threads/{id}")).StatusCode);
    }

    [Fact]
    public async Task MessageRecordShapeAndErrors()
    {
        var id = await NewThread();
        await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = """/tool calculator {"expression":"2*3"}""" });
        JsonArray msgs = [];
        for (var i = 0; i < 100 && msgs.Count < 4; i++)
        {
            await Task.Delay(50);
            msgs = (await _client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}/messages"))!["messages"]!.AsArray();
        }
        var call = msgs[1]!;
        Assert.Equal(System.Text.Json.JsonValueKind.String, call["tool_calls"]!.GetValueKind());
        Assert.Contains("calculator", call["tool_calls"]!.GetValue<string>());
        Assert.Equal("completed", call["status"]!.ToString());
        Assert.NotNull(call["metadata"]);
        var status = (await _client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}"))!["status"]!;
        Assert.Equal("stop_on_response", status["reason"]!.ToString());
        Assert.True(status["since"]!.GetValue<long>() > 1_000_000_000_000_000);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/threads/{id}/messages", new { role = "user", content = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/threads?limit=-1")).StatusCode);
        var unknown = await _client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.NotNull((await unknown.Content.ReadFromJsonAsync<JsonObject>())!["error"]);
        var bad = await _client.PostAsync("/api/threads", new StringContent("{oops", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"/api/threads/{id}/messages/{msgs[1]!["id"]}")).StatusCode);
        var left = (await _client.GetFromJsonAsync<JsonObject>($"/api/threads/{id}/messages"))!["messages"]!.AsArray();
        Assert.DoesNotContain(left, m => m!["role"]!.ToString() == "tool");
    }
}
