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

    /// <summary>Context window size in tokens. Floor: non-embedding models below
    /// 32768 are auto-raised at load (see ModelSlot). Default 65536 ("Normal" tier).</summary>
    [JsonPropertyName("context_size")]
    public uint ContextSize { get; set; } = 65536;

    /// <summary>-1 = auto (null in LLamaSharp).</summary>
    [JsonPropertyName("threads")]
    public int Threads { get; set; } = -1;

    /// <summary>Batch size for inference (logical batch, n_batch). 0 = LLamaSharp
    /// default (512). Chat models tolerate batch > ubatch (llama.cpp splits into
    /// micro-batches); embedding (non-causal) models REQUIRE batch == ubatch —
    /// see UbatchSize and ModelSlot.CreateModelParams for the reconciliation rules.</summary>
    [JsonPropertyName("batch_size")]
    public uint BatchSize { get; set; } = 1024;

    /// <summary>Physical micro-batch size (n_ubatch). 0 = LLamaSharp default (512).
    /// Non-causal (embedding) models require ubatch == batch; when 0 and batch is
    /// set, ModelSlot auto-mirrors ubatch to batch for embedding models so custom
    /// embedding batch sizes load instead of throwing.</summary>
    [JsonPropertyName("ubatch_size")]
    public uint UbatchSize { get; set; } = 0;

    /// <summary>
    /// KV cache quantization: "q8_0" (default — halves KV memory, negligible quality
    /// loss) or "f16". Embedding models always use the model default (f16).
    /// </summary>
    [JsonPropertyName("kv_cache")]
    public string KvCache { get; set; } = "q8_0";

    /// <summary>Flash attention (llama.cpp FA). Default on — faster prefill, lower KV
    /// memory, and required for the long-context tiers to be practical.</summary>
    [JsonPropertyName("flash_attn")]
    public bool FlashAttn { get; set; } = true;

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

    /// <summary>
    /// Execution backend override: "auto" (default), "llamasharp", or "process".
    /// "auto" routes ternary-packed models to the Process backend (see BackendSelector).
    /// </summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "auto";

    /// <summary>
    /// Per-model default output tokens for chat requests that don't set max_tokens.
    /// 0 = use the global inference default. Lets the catalog tune output budget per
    /// model (e.g. thinking models need more room).
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 0;
}