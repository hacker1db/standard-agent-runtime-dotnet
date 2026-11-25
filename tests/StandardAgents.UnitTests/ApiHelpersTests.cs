using StandardAgents.Cli;

namespace StandardAgents.UnitTests;

public sealed class ApiHelpersTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("[::1]", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("localhost.attacker.example", false)]
    [InlineData("10.0.0.5", false)]
    public void LoopbackHosts(string host, bool expected) => Assert.Equal(expected, Api.IsLoopbackHost(host));

    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://127.0.0.1", true)]
    [InlineData("http://attacker.example", false)]
    [InlineData("null", false)]
    [InlineData("file://localhost", false)]
    public void LoopbackOrigins(string origin, bool expected) => Assert.Equal(expected, Api.IsLoopbackOrigin(origin));

    [Fact]
    public void GeneratedTokensAreLongAndUnique()
    {
        var a = Api.NewToken();
        Assert.True(a.Length >= 43, a);
        Assert.NotEqual(a, Api.NewToken());
        Assert.DoesNotContain('=', a);
    }
}
