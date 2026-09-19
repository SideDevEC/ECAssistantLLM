using LLama;
using LLama.Common;
using LLama.Native;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// One loaded model: weights + params + status.
/// Managed by MultiModelHost.
/// </summary>
public sealed class ModelSlot : IDisposable
{
    private readonly ILogger _logger;
    /// <summary>Server root (--root). Model paths resolve strictly inside this directory.</summary>
    private readonly string _rootDir;
    private bool _disposed;

    /// <summary>Unique model ID (used in OpenAI "model" field).</summary>
    public string Id { get; }

    /// <summary>Config this slot was created from.</summary>
    public ModelConfig Config { get; }

    /// <summary>Loaded model weights. null if not yet loaded or disposed.</summary>
    public LLamaWeights? Weights { get; private set; }

    /// <summary>Model params used to load weights.</summary>
    public ModelParams Params { get; private set; }

    /// <summary>Whether weights are loaded and ready.</summary>
    public bool IsLoaded => Weights != null && !_disposed;

    /// <summary>Whether this is an embedding model.</summary>
    public bool IsEmbedding => Config.IsEmbedding;

    /// <summary>Optional embedder instance for embedding models.</summary>
    public LLamaEmbedder? Embedder { get; private set; }

    /// <summary>Vector dimension for embedding models (0 until first embed call).</summary>
    public int EmbeddingDim { get; private set; }

    /// <summary>
    /// Loaded MTMD (mmproj) projector for vision models. null until first use
    /// or when no mmproj_path is configured. Lazy: loaded on first vision request.
    /// Thread-safe: init is locked; a failed load is latched in _mmprojFailed so a
    /// broken mmproj is not retried on every request (per-model reset on Unload).
    /// </summary>
    public MtmdWeights? Mmproj
    {
        get
        {
            if (_mmproj != null) return _mmproj;
            if (_mmprojFailed) return null;
            lock (_mmprojGate)
            {
                if (_mmproj != null) return _mmproj;
                if (_mmprojFailed) return null;
                _mmproj = TryLoadMmproj();
                if (_mmproj == null)
                    _mmprojFailed = true; // sentinel: don't retry a known-bad load forever
                return _mmproj;
            }
        }
    }
    private MtmdWeights? _mmproj;
    private volatile bool _mmprojFailed;
    private readonly object _mmprojGate = new();

    /// <summary>True when this model accepts image input (mmproj configured).</summary>
    public bool SupportsVision => Config.SupportsVision;

    /// <summary>
    /// GPU layers actually applied after the Vulkan DeltaNet-MoE guard.
    /// Differs from Config.GpuLayers when the guard clamped (Vulkan + qwen3_5moe).
    /// </summary>
    public int EffectiveGpuLayers { get; private set; }

    public ModelSlot(string id, ModelConfig config, ILogger logger, string rootDir)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootDir = string.IsNullOrWhiteSpace(rootDir)
            ? throw new ArgumentNullException(nameof(rootDir))
            : Path.GetFullPath(rootDir);

        Params = CreateModelParams(config);
    }

    /// <summary>
    /// Load model weights from disk into memory.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ModelSlot));
        if (Weights != null)
            return;

        var resolvedPath = ResolveModelPath(Config.Path, _rootDir);

        // Vulkan guard: hybrid DeltaNet-MoE models crash on partial GPU offload under
        // Vulkan (llama.cpp #26945). Compute the effective layer count before load.
        var decision = GpuLayerGuard.Compute(
            Config.GpuLayers,
            GgufArchitectureReader.ReadArchitecture(resolvedPath),
            VulkanAvailabilityProbe.IsVulkanPrimary());
        if (decision.Clamped)
            _logger.Warn("ModelSlot", decision.Reason!);
        EffectiveGpuLayers = decision.EffectiveGpuLayers;

        Params = CreateModelParams(Config, resolvedPath, decision.EffectiveGpuLayers);

        try
        {
            Weights = await LLamaWeights.LoadFromFileAsync(Params);

            if (Config.IsEmbedding)
            {
                Embedder = new LLamaEmbedder(Weights, Params);
                // Determine embedding dimension from a test call
                var testEmbeds = await Embedder.GetEmbeddings("dimension test");
                EmbeddingDim = testEmbeds.Single().Length;
            }

            _logger.Info("ModelSlot", $"Loaded model '{Id}' from {resolvedPath} " +
                $"(gpu_layers={EffectiveGpuLayers}, ctx={Config.ContextSize}" +
                (Config.IsEmbedding ? $", embed_dim={EmbeddingDim}" : "") + ")");
        }
        catch (Exception ex)
        {
            _logger.Error("ModelSlot", $"Failed to load model '{Id}' from {resolvedPath}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Unload weights (frees memory) but keep the slot (can re-load later).
    /// </summary>
    public void Unload()
    {
        Embedder?.Dispose();
        Embedder = null;
        _mmproj?.Dispose();
        _mmproj = null;
        // Allow a retry after the slot is re-loaded (e.g. mmproj file fixed on disk).
        _mmprojFailed = false;
        Weights?.Dispose();
        Weights = null;
        _logger.Info("ModelSlot", $"Unloaded model '{Id}'");
    }

    private MtmdWeights? TryLoadMmproj()
    {
        if (!SupportsVision || _disposed || Weights == null) return null;
        try
        {
            var path = Config.MmprojPath!;
            if (!Path.IsPathRooted(path))
                path = ResolveModelPath(path, _rootDir);
            var mtmd = MtmdWeights.LoadFromFile(path, Weights!, MtmdContextParams.Default());
            _logger.Info("ModelSlot", $"Loaded mmproj projector '{Path.GetFileName(path)}' for model '{Id}' — vision enabled");
            return mtmd;
        }
        catch (Exception ex)
        {
            _logger.Error("ModelSlot", $"Failed to load mmproj for '{Id}': {ex.Message} — vision disabled");
            return null;
        }
    }

    private static ModelParams CreateModelParams(ModelConfig config, string? resolvedPath = null, int? effectiveGpuLayers = null)
    {
        var path = resolvedPath ?? config.Path;
        var mp = new ModelParams(path)
        {
            GpuLayerCount = Math.Clamp(effectiveGpuLayers ?? config.GpuLayers, 0, 100),
            ContextSize = config.ContextSize,
            Threads = config.Threads == -1 ? null : config.Threads,
        };

        if (config.BatchSize > 0)
            mp.BatchSize = config.BatchSize;

        if (config.IsEmbedding)
        {
            // Invariant culture: config values must not be reshaped by the OS locale
            // (e.g. Turkish 'I' would break the mean/cls/last matches below).
            mp.PoolingType = config.PoolingType.ToLowerInvariant() switch
            {
                "mean" => LLamaPoolingType.Mean,
                "cls" => LLamaPoolingType.CLS,
                "last" => LLamaPoolingType.Last,
                "none" => LLamaPoolingType.None,
                _ => LLamaPoolingType.Mean
            };
        }

        return mp;
    }

    private static string ResolveModelPath(string path, string rootDir)
    {
        // ROOT-ONLY contract: absolute paths are allowed only inside the root
        // (warn+fail otherwise); relative paths resolve only as {root}/{path} or
        // {root}/models/{filename}. No AppContext.BaseDirectory / CWD fallback.
        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(full, Path.GetFullPath(rootDir), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Model path '{path}' is outside the server root '{rootDir}' — refusing to load.");
            }
            return full;
        }

        var inRoot = Path.Combine(rootDir, path);
        if (File.Exists(inRoot))
            return inRoot;

        var inModels = Path.Combine(rootDir, "models", Path.GetFileName(path));
        if (File.Exists(inModels))
            return inModels;

        return inRoot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}