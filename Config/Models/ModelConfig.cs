using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Configuration for a single model hosted by the server.
/// </summary>
public sealed class ModelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 0;

    [JsonPropertyName("context_size")]
    public uint ContextSize { get; set; } = 4096;

    /// <summary>-1 = auto (null in LLamaSharp).</summary>
    [JsonPropertyName("threads")]
    public int Threads { get; set; } = -1;

    /// <summary>Batch size for inference. 0 = LLamaSharp default.</summary>
    [JsonPropertyName("batch_size")]
    public uint BatchSize { get; set; } = 0;

    /// <summary>True if this is an embedding model (uses LLamaEmbedder, not chat executor).</summary>
    [JsonPropertyName("is_embedding")]
    public bool IsEmbedding { get; set; } = false;

    /// <summary>Pooling type for embedding models. "mean" (default), "cls", "last", "none".</summary>
    [JsonPropertyName("pooling_type")]
    public string PoolingType { get; set; } = "mean";

    /// <summary>
    /// Optional path to an mmproj projector file for vision models (MTMD).
    /// When set, the model accepts image content parts in chat completion requests.
    /// </summary>
    [JsonPropertyName("mmproj_path")]
    public string? MmprojPath { get; set; }

    /// <summary>True when vision is enabled via mmproj_path.</summary>
    [JsonIgnore]
    public bool SupportsVision => !string.IsNullOrWhiteSpace(MmprojPath);
}