using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

public sealed class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "embeddings";

    [JsonPropertyName("input")]
    public string Input { get; set; } = "";
}

public sealed class EmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("data")]
    public List<EmbeddingData> Data { get; set; } = new();
}

public sealed class EmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "embedding";

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();

    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;
}