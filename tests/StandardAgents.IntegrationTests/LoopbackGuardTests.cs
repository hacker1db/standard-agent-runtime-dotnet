using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using StandardAgents.Cli;
using StandardAgents.Runtime;

namespace StandardAgents.IntegrationTests;

/// <summary>On a loopback bind, only loopback Host and Origin values reach the API (DNS-rebinding defense).</summary>
[Trait("Category", "Integration")]
public sealed class LoopbackGuardTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("sa-guard-").FullName;
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
        _app = Api.Build(rt, "secret", b => b.WebHost.UseTestServer(), loopbackOnly: true);
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    async Task<HttpStatusCode> Get(string host, string? origin = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        req.Headers.Host = host;
        if (origin is not null) req.Headers.Add("Origin", origin);
        return (await _client.SendAsync(req)).StatusCode;
    }

    [Theory]
    [InlineData("localhost:8787")]
    [InlineData("127.0.0.1:8787")]
    [InlineData("[::1]:8787")]
    public async Task LoopbackHostIsAccepted(string host) => Assert.Equal(HttpStatusCode.OK, await Get(host));

    [Fact]
    public async Task RebindingHostIsRejected() => Assert.Equal(HttpStatusCode.Forbidden, await Get("attacker.example:8787"));

    [Fact]
    public async Task CrossSiteOriginIsRejected() =>
        Assert.Equal(HttpStatusCode.Forbidden, await Get("127.0.0.1:8787", "http://attacker.example"));

    [Fact]
    public async Task LoopbackOriginOnAnotherPortIsAccepted() =>
        Assert.Equal(HttpStatusCode.OK, await Get("127.0.0.1:8787", "http://localhost:5173")); // vite dev server
}
