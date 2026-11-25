using StandardAgents.Runtime;
using System.Text.Json.Nodes;

namespace StandardAgents.Tests;

public sealed class CliProviderTests
{
    [Fact]
    public void ParsesToolCallWithBraceInsideString()
    {
        var r = CliProvider.ParseReply("```json\n{\"tool_calls\":[{\"name\":\"calc\",\"arguments\":{\"x\":\"}\"}}]}\n```", true);
        Assert.Null(r.Text);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal("calc", call.Name);
        Assert.Equal("}", JsonNode.Parse(call.Arguments)!["x"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesContent() => Assert.Equal("hi", CliProvider.ParseReply("Sure: {\"content\":\"hi\"}", true).Text);

    [Fact]
    public void FallsBackToPlainText()
    {
        var r = CliProvider.ParseReply("  plain words  ", true);
        Assert.Equal("plain words", r.Text);
        Assert.Empty(r.ToolCalls);
    }
}
