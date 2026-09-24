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
}