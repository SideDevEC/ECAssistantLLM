using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Logging configuration for the server.
/// </summary>
public sealed class LoggingSection
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("file")]
    public string File { get; set; } = "ecassistant-llm.log";
}