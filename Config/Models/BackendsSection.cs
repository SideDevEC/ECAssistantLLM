using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Configuration for the backend subsystem: where the pre-installed external
/// runtime builds and model weights live. ECAssistantLLM never downloads —
/// the setup wizard installs everything (see install-manifest.json shipped
/// with the server package).
/// </summary>
public sealed class BackendsSection
{
    /// <summary>Root directory for external backend runtimes. Resolved against the server root when relative.</summary>
    [JsonPropertyName("backends_root")]
    public string BackendsRoot { get; set; } = "backends";

    /// <summary>Directory holding installed model weights. Resolved against the server root when relative.</summary>
    [JsonPropertyName("models_root")]
    public string ModelsRoot { get; set; } = "models";
}
