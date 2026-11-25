using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StandardAgents.Runtime;

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null, string? Name = null);

public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record ModelRequest(ModelDef Model, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec> Tools, string ToolChoice);

public sealed record ModelResponse(string? Text, IReadOnlyList<ToolCall> ToolCalls);

public interface IProvider
{
    Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct);
}

/// <summary>Deterministic provider: "/tool NAME JSON" as last user message emits that call, else echoes.</summary>
public sealed partial class MockProvider : IProvider
{
    [GeneratedRegex(@"^/tool\s+(\S+)\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex ToolCommand();

    public Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct)
    {
        var last = req.Messages.LastOrDefault(m => m.Role != "system");
        if (last is { Role: "user" } && ToolCommand().Match(last.Content ?? "") is { Success: true } m)
        {
            var args = string.IsNullOrWhiteSpace(m.Groups[2].Value) ? "{}" : m.Groups[2].Value.Trim();
            return Task.FromResult(new ModelResponse(null, [new ToolCall("call_" + Guid.NewGuid().ToString("N")[..12], m.Groups[1].Value, args)]));
        }
        var lastUser = req.Messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        return Task.FromResult(new ModelResponse($"echo: {lastUser}", []));
    }
}

/// <summary>Any OpenAI-compatible /chat/completions endpoint (OpenAI, Ollama, vLLM, ...).</summary>
public sealed class OpenAiProvider(HttpClient http) : IProvider
{
    public async Task<ModelResponse> CompleteAsync(ModelRequest req, CancellationToken ct)
    {
        var baseUrl = (req.Model.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        var body = new JsonObject
        {
            ["model"] = req.Model.Model,
            ["messages"] = new JsonArray(req.Messages.Select(ToJson).ToArray<JsonNode?>()),
        };
        if (req.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(req.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            }).ToArray());
            body["tool_choice"] = req.ToolChoice;
        }
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var key = Environment.GetEnvironmentVariable(req.Model.ApiKeyEnv ?? "OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await http.SendAsync(msg, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"provider HTTP {(int)resp.StatusCode}: {text}");
        var choice = JsonNode.Parse(text)?["choices"]?[0]?["message"] ?? throw new InvalidOperationException("provider returned no choices");
        var calls = (choice["tool_calls"] as JsonArray ?? []).Select(tc => new ToolCall(
            tc!["id"]?.GetValue<string>() ?? "call_" + Guid.NewGuid().ToString("N")[..12],
            tc["function"]!["name"]!.GetValue<string>(),
            tc["function"]!["arguments"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : tc["function"]!["arguments"]?.ToJsonString() ?? "{}")).ToList();
        return new ModelResponse(choice["content"]?.GetValue<string>(), calls);
    }

    static JsonNode ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content ?? "" };
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = ToolCallsJson(m.ToolCalls);
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        if (m.Name is not null && m.Role == "tool") o["name"] = m.Name;
        return o;
    }

    public static JsonArray ToolCallsJson(IEnumerable<ToolCall> calls) => new(calls.Select(c => (JsonNode)new JsonObject
    {
        ["id"] = c.Id, ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
    }).ToArray());

    public static List<ToolCall> ParseToolCalls(string? json) => json is null ? [] :
        JsonNode.Parse(json)!.AsArray().Select(n => new ToolCall(n!["id"]!.GetValue<string>(),
            n["function"]!["name"]!.GetValue<string>(), n["function"]!["arguments"]!.GetValue<string>())).ToList();
}
