using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// One entry of the OpenAI-native `tools` request field: a function the client
/// declares, with its JSON-schema parameters. The server grammar-constrains the
/// model's output against these schemas and returns a typed tool_calls message.
/// </summary>
public sealed class OpenAiToolSpec
{
    /// <summary>Always "function" in OpenAI chat completions.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionSpec? Function { get; set; }

    [JsonIgnore]
    public bool IsValid => Type == "function"
        && Function is not null
        && !string.IsNullOrWhiteSpace(Function.Name);
}

public sealed class OpenAiFunctionSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Raw JSON-schema parameters document (usually {"type":"object",...}).</summary>
    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary>
/// Decoded tool call emitted in the response's message.tool_calls (OpenAI shape).
/// ArgumentsJson holds the raw JSON of the arguments object (to be serialized as a
/// string per the OpenAI wire format).
/// Stateless utility — no mutable state.
/// </summary>
public sealed record ToolCall(string Name, string ArgumentsJson);

public sealed class InvalidToolCallException : Exception
{
    public InvalidToolCallException(string message) : base(message) { }
}
