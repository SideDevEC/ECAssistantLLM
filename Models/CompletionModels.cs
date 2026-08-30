using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// OpenAI-compatible text completion request.
/// POST /v1/completions
/// </summary>
public sealed class CompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "main";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("repeat_penalty")]
    public float? RepeatPenalty { get; set; }

    /// <summary>ECAssistant extension: routes to specific KV cache session.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

/// <summary>
/// Non-streaming text completion response.
/// </summary>
public sealed class CompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "text_completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<CompletionChoice> Choices { get; set; } = new();
}

public sealed class CompletionChoice
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("logprobs")]
    public object? Logprobs { get; set; }
}

/// <summary>
/// SSE streaming chunk for text completions.
/// object = "text_completion" (different from chat.completion.chunk)
/// </summary>
public sealed class CompletionChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "text_completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<CompletionChunkChoice> Choices { get; set; } = new();

    public static CompletionChunk Delta(string model, string text) => new()
    {
        Model = model,
        Choices = new() { new() { Text = text } }
    };

    public static CompletionChunk Finish(string model) => new()
    {
        Model = model,
        Choices = new() { new() { Text = "", FinishReason = "stop" } }
    };
}

public sealed class CompletionChunkChoice
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}