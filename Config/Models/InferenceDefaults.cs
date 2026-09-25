using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Default inference parameters. Can be overridden per request.
/// </summary>
public sealed class InferenceDefaults
{
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;

    [JsonPropertyName("top_p")]
    public float TopP { get; set; } = 0.95f;

    [JsonPropertyName("top_k")]
    public int TopK { get; set; } = 40;

    [JsonPropertyName("repeat_penalty")]
    public float RepeatPenalty { get; set; } = 1.1f;

    /// <summary>
    /// Max parallel tool calls per response. Bounded in the tool-call GBNF grammar
    /// so the model can never loop objects past this count — an unbounded array
    /// lets low-temp models repeat tool calls until max_tokens truncates the JSON
    /// mid-string (flaky 422s). Also guarantees the array closes within budget.
    /// </summary>
    [JsonPropertyName("max_parallel_tool_calls")]
    public int MaxParallelToolCalls { get; set; } = 3;
}