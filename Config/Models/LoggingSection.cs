using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Logging configuration for the server.
/// </summary>
public sealed class LoggingSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("file")]
    public string File { get; set; } = "ecassistant-llm.log";

    /// <summary>
    /// Component filter — when non-empty, only Debug/Info messages from these
    /// tags are logged. Error/Warn always pass. Empty = all components.
    /// Known: router, grammar, sampling, sessions, batch, stop, stateless, model, server.
    /// </summary>
    [JsonPropertyName("components")]
    public string[] Components { get; set; } = Array.Empty<string>();

    /// <summary>Resolve to enum. Unknown → Info. None when disabled.</summary>
    public LogLevel ResolvedLevel =>
        Enabled
            ? Level.ToLowerInvariant() switch
            {
                "debug" => LogLevel.Debug,
                "warn" => LogLevel.Warn,
                "error" => LogLevel.Error,
                _ => LogLevel.Info
            }
            : LogLevel.None;

    public bool IsComponentEnabled(string tag)
    {
        if (Components is null or { Length: 0 }) return true;
        foreach (var c in Components)
            if (tag.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}