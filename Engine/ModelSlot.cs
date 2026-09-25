using ECAssistantInference.Abstractions;
using ECAssistantInference.Exceptions;
using ECAssistantInference.Implementation;
using ECAssistantInference.Models;
using InfModelConfig = ECAssistantInference.Models.ModelConfig;
using InfContextConfig = ECAssistantInference.Models.ContextConfig;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;
using KvType = ECAssistantInference.Models.KvType;
using PoolingType = ECAssistantInference.Models.PoolingType;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// One loaded model: inference model + config + status.
/// Managed by MultiModelHost.
/// </summary>
public sealed class ModelSlot : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _rootDir;
    private bool _disposed;

    public string Id { get; }
    public static uint MinChatContextSize { get; } = LoadFloor();

    private static uint LoadFloor()
    {
        var raw = Environment.GetEnvironmentVariable("ECA_MIN_CONTEXT");
        return uint.TryParse(raw, out var v) ? v : 32768u;
    }

    public ECAssistant.LLM.Config.ModelConfig Config { get; }

    /// <summary>Loaded inference model. null if not yet loaded or disposed.</summary>
    public IInferenceModel? Model { get; private set; }

    /// <summary>Whether model is loaded and ready.</summary>
    public bool IsLoaded => Model != null && !_disposed;

    public bool IsEmbedding => Config.IsEmbedding;

    /// <summary>Vector dimension for embedding models (0 until first embed call).</summary>
    public int EmbeddingDim { get; private set; }

    /// <summary>Loaded MTMD vision encoder. null until first use or no mmproj configured.</summary>
    public IVisionEncoder? Vision
    {
        get
        {
            if (_vision != null) return _vision;
            if (_visionFailed) return null;
            lock (_visionGate)
            {
                if (_vision != null) return _vision;
                if (_visionFailed) return null;
                _vision = TryLoadVision();
                if (_vision == null)
                    _visionFailed = true;
                return _vision;
            }
        }
    }
    private IVisionEncoder? _vision;
    private volatile bool _visionFailed;
    private readonly object _visionGate = new();

    public bool SupportsVision => Config.SupportsVision;
    public int EffectiveGpuLayers { get; private set; }

    public ModelSlot(string id, ECAssistant.LLM.Config.ModelConfig config, ILogger logger, string rootDir)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootDir = string.IsNullOrWhiteSpace(rootDir)
            ? throw new ArgumentNullException(nameof(rootDir))
            : Path.GetFullPath(rootDir);
    }

    public async Task LoadAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ModelSlot));
        if (Model != null)
            return;

        var resolvedPath = ResolveModelPath(Config.Path, _rootDir);

        // Vulkan guard (kept from LLamaSharp era — still relevant for ECAssistantInference)
        var decision = GpuLayerGuard.Compute(
            Config.GpuLayers,
            GgufArchitectureReader.ReadArchitecture(resolvedPath),
            VulkanAvailabilityProbe.IsVulkanPrimary());
        if (decision.Clamped)
            _logger.Warn("ModelSlot", decision.Reason!);
        EffectiveGpuLayers = decision.EffectiveGpuLayers;

        // v15 hard floor
        if (!Config.IsEmbedding && Config.ContextSize < MinChatContextSize)
        {
            _logger.Warn("ModelSlot", $"Model '{Id}' context_size {Config.ContextSize} below hard floor {MinChatContextSize} — raising to {MinChatContextSize}.");
            Config.ContextSize = MinChatContextSize;
        }

        try
        {
            var modelConfig = new InfModelConfig
            {
                Path = resolvedPath,
                GpuLayers = EffectiveGpuLayers,
                Threads = Config.Threads,
                FlashAttention = Config.FlashAttn,
                KvCacheType = ParseKvType(Config.KvCache),
            };

            Model = NativeInferenceModel.Load(modelConfig);

            if (Config.IsEmbedding)
            {
                // Determine embedding dimension from a test call
                using var testCtx = Model.CreateContext(new ECAssistantInference.Models.ContextConfig
                {
                    ContextSize = Config.ContextSize,
                    BatchSize = Config.BatchSize > 0 ? (uint)Config.BatchSize : 512,
                    SeqMax = 1,
                    PoolingType = ParsePoolingType(Config.PoolingType),
                });
                var testEmbeds = Model.GetEmbeddings(testCtx, "dimension test");
                EmbeddingDim = testEmbeds.Length;
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

    public void Unload()
    {
        _vision?.Dispose();
        _vision = null;
        _visionFailed = false;
        Model?.Dispose();
        Model = null;
        _logger.Info("ModelSlot", $"Unloaded model '{Id}'");
    }

    private IVisionEncoder? TryLoadVision()
    {
        if (!SupportsVision || _disposed || Model == null) return null;
        try
        {
            var path = Config.MmprojPath!;
            if (!Path.IsPathRooted(path))
                path = ResolveModelPath(path, _rootDir);
            var vision = Model.LoadVisionEncoder(path);
            _logger.Info("ModelSlot", $"Loaded mmproj projector '{Path.GetFileName(path)}' for model '{Id}' — vision enabled");
            return vision;
        }
        catch (Exception ex)
        {
            _logger.Error("ModelSlot", $"Failed to load mmproj for '{Id}': {ex.Message} — vision disabled");
            return null;
        }
    }

    private static KvType ParseKvType(string kvCache)
    {
        if (string.IsNullOrEmpty(kvCache) || kvCache.Equals("f16", StringComparison.OrdinalIgnoreCase))
            return KvType.F16;
        if (kvCache.Equals("q8_0", StringComparison.OrdinalIgnoreCase))
            return KvType.Q8_0;
        if (kvCache.Equals("q4_0", StringComparison.OrdinalIgnoreCase))
            return KvType.Q4_0;
        if (kvCache.Equals("q4_1", StringComparison.OrdinalIgnoreCase))
            return KvType.Q4_1;
        return KvType.F16;
    }

    private static PoolingType ParsePoolingType(string pooling)
    {
        return pooling?.ToLowerInvariant() switch
        {
            "mean" => PoolingType.Mean,
            "cls" => PoolingType.Cls,
            "last" => PoolingType.Last,
            "none" => PoolingType.None,
            _ => PoolingType.Mean,
        };
    }

    private static string ResolveModelPath(string path, string rootDir)
    {
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
