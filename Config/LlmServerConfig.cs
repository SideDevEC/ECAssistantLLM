using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Root server configuration. Deserialized from llm-server.json.
/// </summary>
public sealed class LlmServerConfig
{
    [JsonPropertyName("server")]
    public ServerSection Server { get; set; } = new();

    [JsonPropertyName("models")]
    public List<ModelConfig> Models { get; set; } = new();

    [JsonPropertyName("inference")]
    public InferenceDefaults Inference { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingSection Logging { get; set; } = new();

    /// <summary>
    /// Load config from a JSON file path. Throws if file is missing or invalid.
    /// </summary>
    public static LlmServerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"LLM server config not found: {path}");

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<LlmServerConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize llm-server.json");

        config.Validate();
        return config;
    }

    /// <summary>
    /// Try load config — returns null on failure with error message.
    /// </summary>
    public static (LlmServerConfig? config, string? error) TryLoad(string path)
    {
        try
        {
            return (Load(path), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private void Validate()
    {
        if (Server.Port < 1 || Server.Port > 65535)
            throw new InvalidOperationException($"Invalid port: {Server.Port}");

        if (Models.Count == 0)
            throw new InvalidOperationException("No models configured");

        var ids = Models.Select(m => m.Id).ToList();
        var duplicates = ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate model IDs: {string.Join(", ", duplicates)}");

        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                throw new InvalidOperationException("Model missing ID");
            if (string.IsNullOrWhiteSpace(model.Path))
                throw new InvalidOperationException($"Model '{model.Id}' missing path");
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}