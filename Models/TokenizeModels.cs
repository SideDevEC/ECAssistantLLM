using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

public sealed class TokenizeRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "main";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class TokenizeResponse
{
    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    [JsonPropertyName("token_ids")]
    public int[] TokenIds { get; set; } = Array.Empty<int>();
}