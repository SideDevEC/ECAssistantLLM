using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// SSE streaming chunk (OpenAI format).
/// </summary>
public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<ChunkChoice> Choices { get; set; } = new();

    public static ChatCompletionChunk Delta(string model, string content) => new()
    {
        Model = model,
        Choices = new() { new() { Delta = new() { Content = content } } }
    };

    public static ChatCompletionChunk Finish(string model) => new()
    {
        Model = model,
        Choices = new() { new() { Delta = new(), FinishReason = "stop" } }
    };
}

public sealed class ChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;

    [JsonPropertyName("delta")]
    public ChunkDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class ChunkDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}