using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// v13 structured decision envelope — the grammar-forced output shape.
/// Exactly one of <see cref="Answer"/> or <see cref="ToolCalls"/> is populated.
/// </summary>
public sealed class DecisionEnvelope
{
    /// <summary>Brief model reasoning. Always present.</summary>
    [JsonPropertyName("thinking")]
    public string Thinking { get; set; } = "";

    /// <summary>User-facing reply. Populated when the model decided to answer directly.</summary>
    [JsonPropertyName("answer")]
    public string? Answer { get; set; }

    /// <summary>Tool invocations. Populated when the model decided to call tools.</summary>
    [JsonPropertyName("toolcalls")]
    public List<DecisionToolCall>? ToolCalls { get; set; }

    [JsonIgnore]
    public bool HasAnswer => !string.IsNullOrEmpty(Answer);

    [JsonIgnore]
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}

/// <summary>A single structured tool call.</summary>
public sealed class DecisionToolCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Argument map. Values are strings; the client coerces types (same as the legacy tag protocol).</summary>
    [JsonPropertyName("args")]
    public Dictionary<string, string> Args { get; set; } = new();
}
