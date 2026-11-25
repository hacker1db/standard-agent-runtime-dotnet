using System.Text.Json.Nodes;
using StandardAgents.Runtime;

namespace StandardAgents.IntegrationTests;

/// <summary>Runs the subscription providers against <see cref="FakeCli"/>: no login, no network, no API key.</summary>
[Trait("Category", "Integration")]
public sealed class CliProviderProcessTests : IDisposable
{
    readonly FakeCli _cli = new();

    public void Dispose() => _cli.Dispose();

    static ModelRequest Request(string provider, string model = "sonnet", bool tools = false) => new(
        new ModelDef("m", provider, model, null, null, []),
        [new ChatMessage("system", "be brief"), new ChatMessage("user", "hello")],
        tools ? [new ToolSpec("calc", "adds", new JsonObject { ["type"] = "object" })] : [],
        "auto");

    [Fact]
    public async Task ClaudePassesHeadlessArgumentsAndTranscriptOnStdin()
    {
        _cli.Reply(FakeCli.ClaudeJson("hi there"));
        var r = await _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli"), CancellationToken.None);

        Assert.Equal("hi there", r.Text);
        var args = _cli.Args();
        Assert.Equal(["-p", "--output-format", "json", "--tools", "", "--strict-mcp-config", "--no-session-persistence",
            "--setting-sources", "", "--system-prompt", "be brief", "--model", "sonnet"], args);
        Assert.Equal("[user]\nhello\n\n[assistant]\n", _cli.Stdin());
    }

    [Fact]
    public async Task DefaultModelOmitsTheModelFlag()
    {
        _cli.Reply(FakeCli.ClaudeJson("ok"));
        await _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli", model: "default"), CancellationToken.None);
        Assert.DoesNotContain("--model", _cli.Args());
    }

    [Fact]
    public async Task ClaudeToolCallReplyBecomesToolCalls()
    {
        _cli.Reply(FakeCli.ClaudeJson("""{"tool_calls":[{"name":"calc","arguments":{"a":1}}]}"""));
        var r = await _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli", tools: true), CancellationToken.None);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal("calc", call.Name);
        Assert.Contains("# Reply format (mandatory)", string.Join("\n", _cli.Args()));
    }

    [Fact]
    public async Task ClaudeIsErrorFails()
    {
        _cli.Reply(FakeCli.ClaudeJson("usage limit reached", isError: true));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli"), CancellationToken.None));
        Assert.Contains("usage limit reached", e.Message);
    }

    [Fact]
    public async Task ClaudeNonJsonFails()
    {
        _cli.Reply("not json");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli"), CancellationToken.None));
        Assert.Contains("non-JSON", e.Message);
    }

    [Fact]
    public async Task NonZeroExitFailsWithStderr()
    {
        _cli.ExitWith(3);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli"), CancellationToken.None));
        Assert.Contains("exited 3", e.Message);
        Assert.Contains("fake failure", e.Message);
    }

    [Fact]
    public async Task SlowCliTimesOut()
    {
        _cli.SleepFor(30).Reply(FakeCli.ClaudeJson("late"));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            _cli.Provider("claude-cli", TimeSpan.FromMilliseconds(500)).CompleteAsync(Request("claude-cli"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingBinaryFailsWithHint()
    {
        var p = new CliProvider("claude-cli", Path.Combine(_cli.Dir, "does-not-exist"));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => p.CompleteAsync(Request("claude-cli"), CancellationToken.None));
        Assert.Contains("is it installed and on PATH", e.Message);
    }

    [Fact]
    public async Task CodexReadsFinalMessageFileAndDeletesIt()
    {
        _cli.Reply("""{"content":"from codex"}""");
        var r = await _cli.Provider("codex-cli").CompleteAsync(Request("codex-cli", model: "default", tools: true), CancellationToken.None);

        Assert.Equal("from codex", r.Text);
        var args = _cli.Args();
        Assert.Equal(["exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never", "-o"], args[..8]);
        Assert.Equal("-", args[^1]);
        Assert.DoesNotContain("-m", args);
        Assert.False(File.Exists(args[8]));
        Assert.StartsWith("be brief", _cli.Stdin());
    }

    [Fact]
    public async Task CodexWithoutFinalMessageFails()
    {
        _cli.NoOutputFile().Reply("ignored");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _cli.Provider("codex-cli").CompleteAsync(Request("codex-cli"), CancellationToken.None));
        Assert.Contains("no final message", e.Message);
    }

    [Fact]
    public async Task ApiKeysAreNotPassedToTheCliAndItRunsInAPrivateEmptyDir()
    {
        const string var = "ANTHROPIC_API_KEY";
        var before = Environment.GetEnvironmentVariable(var);
        Environment.SetEnvironmentVariable(var, "sk-should-not-leak");
        try
        {
            _cli.Reply(FakeCli.ClaudeJson("ok"));
            await _cli.Provider("claude-cli").CompleteAsync(Request("claude-cli"), CancellationToken.None);
        }
        finally { Environment.SetEnvironmentVariable(var, before); }

        var env = _cli.Env();
        foreach (var k in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT" })
            Assert.False(env.ContainsKey(k), $"{k} reached the CLI");
        var cwd = _cli.Cwd();
        Assert.StartsWith("sa-cli-", Path.GetFileName(cwd));
        Assert.NotEqual(Path.GetFullPath(Path.GetTempPath()).TrimEnd('/'), cwd.TrimEnd('/'));
        Assert.Equal("drwx------", _cli.Mode());
        Assert.False(Directory.Exists(cwd), "work dir must be deleted after the call");
    }

    [Fact]
    public void UnknownCliKindIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => new CliProvider("gemini-cli"));
    }
}
