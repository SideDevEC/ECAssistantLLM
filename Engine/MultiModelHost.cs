using LLama;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Manages multiple loaded models (at least 2: main + embeddings).
/// Models are loaded once and shared across all client sessions.
/// </summary>
public sealed class MultiModelHost : IDisposable
{
    private readonly Dictionary<string, ModelSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Model IDs that are loaded.</summary>
    public IReadOnlyList<string> LoadedModelIds => _slots.Values
        .Where(s => s.IsLoaded)
        .Select(s => s.Id)
        .ToList();

    /// <summary>Number of loaded models.</summary>
    public int LoadedCount => _slots.Values.Count(s => s.IsLoaded);

    /// <summary>Main chat model ID (first non-embedding model in config).</summary>
    public string MainModelId { get; }

    /// <summary>Embedding model ID (first embedding model in config, or null).</summary>
    public string? EmbeddingModelId { get; }

    public MultiModelHost(LlmServerConfig config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        MainModelId = config.Models.FirstOrDefault(m => !m.IsEmbedding)?.Id
            ?? throw new InvalidOperationException("No chat model configured");
        EmbeddingModelId = config.Models.FirstOrDefault(m => m.IsEmbedding)?.Id;
    }

    /// <summary>
    /// Load all configured models into memory.
    /// </summary>
    public void LoadAll()
    {
        foreach (var modelConfig in _config.Models)
        {
            var slot = new ModelSlot(modelConfig.Id, modelConfig, _logger);
            slot.Load();
            _slots[modelConfig.Id] = slot;
        }

        _logger.Info("MultiModelHost", $"Loaded {LoadedCount} model(s): {string.Join(", ", LoadedModelIds)}");
    }

    /// <summary>
    /// Get a loaded model slot by ID. Throws if not found or not loaded.
    /// </summary>
    public ModelSlot GetSlot(string modelId)
    {
        if (!_slots.TryGetValue(modelId, out var slot))
            throw new InvalidOperationException($"Model '{modelId}' not found");

        if (!slot.IsLoaded)
            throw new InvalidOperationException($"Model '{modelId}' not loaded");

        return slot;
    }

    /// <summary>
    /// Try get a loaded slot. Returns null if not found/not loaded.
    /// </summary>
    public ModelSlot? TryGetSlot(string modelId)
    {
        if (!_slots.TryGetValue(modelId, out var slot)) return null;
        return slot.IsLoaded ? slot : null;
    }

    /// <summary>
    /// Get the main chat model slot.
    /// </summary>
    public ModelSlot GetMainSlot() => GetSlot(MainModelId);

    /// <summary>
    /// Get the embedding model slot, if configured.
    /// </summary>
    public ModelSlot? GetEmbeddingSlot()
        => EmbeddingModelId != null ? TryGetSlot(EmbeddingModelId) : null;

    /// <summary>
    /// Load a new model at runtime.
    /// </summary>
    public bool TryLoadModel(ModelConfig modelConfig)
    {
        if (_slots.ContainsKey(modelConfig.Id))
        {
            _logger.Warn("MultiModelHost", $"Model '{modelConfig.Id}' already exists");
            return false;
        }

        try
        {
            var slot = new ModelSlot(modelConfig.Id, modelConfig, _logger);
            slot.Load();
            _slots[modelConfig.Id] = slot;
            _logger.Info("MultiModelHost", $"Loaded new model '{modelConfig.Id}' at runtime");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("MultiModelHost", $"Failed to load model '{modelConfig.Id}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unload a model at runtime.
    /// </summary>
    public bool TryUnloadModel(string modelId)
    {
        if (!_slots.TryGetValue(modelId, out var slot))
            return false;

        slot.Unload();
        _logger.Info("MultiModelHost", $"Unloaded model '{modelId}' at runtime");
        return true;
    }

    /// <summary>
    /// List all model slots with status info.
    /// </summary>
    public IReadOnlyList<ModelInfo> GetModelInfoList()
    {
        return _slots.Values.Select(s => new ModelInfo(
            s.Id,
            s.Config.Path,
            s.IsLoaded,
            s.IsEmbedding,
            s.Config.GpuLayers,
            s.Config.ContextSize,
            s.EmbeddingDim
        )).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var slot in _slots.Values)
            slot.Dispose();

        _slots.Clear();
    }
}

/// <summary>
/// Read-only model status info for API responses.
/// </summary>
public sealed record ModelInfo(
    string Id,
    string Path,
    bool IsLoaded,
    bool IsEmbedding,
    int GpuLayers,
    uint ContextSize,
    int EmbeddingDim
);