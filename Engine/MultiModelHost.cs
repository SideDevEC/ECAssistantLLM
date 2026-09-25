using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;

namespace ECAssistant.LLM.Engine;

public sealed class MultiModelHost : IDisposable
{
    private readonly Dictionary<string, ModelSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _slotsLock = new();
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly BackendSelector _backendSelector;
    private readonly string _rootDir;
    private bool _disposed;

    public IReadOnlyList<string> LoadedModelIds => _slots.Values
        .Where(s => s.IsLoaded)
        .Select(s => s.Id)
        .ToList();

    public int LoadedCount => _slots.Values.Count(s => s.IsLoaded);
    public string MainModelId { get; }
    public string? EmbeddingModelId { get; }

    public MultiModelHost(LlmServerConfig config, ILogger logger, string rootDir, BackendSelector? backendSelector = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backendSelector = backendSelector ?? new BackendSelector();
        _rootDir = string.IsNullOrWhiteSpace(rootDir)
            ? throw new ArgumentNullException(nameof(rootDir))
            : Path.GetFullPath(rootDir);

        MainModelId = ResolveMainModelId(config);
        EmbeddingModelId = config.Models.FirstOrDefault(m => m.IsEmbedding)?.Id;
    }

    private string ResolveMainModelId(LlmServerConfig config)
    {
        var main = config.Models.FirstOrDefault(m =>
            !m.IsEmbedding && _backendSelector.Select(m) == ModelBackendKind.Native)
            ?? config.Models.FirstOrDefault(m => !m.IsEmbedding)
            ?? throw new InvalidOperationException("No chat model configured");
        return main.Id;
    }

    public async Task LoadAllAsync()
    {
        var failures = new List<string>();
        foreach (var modelConfig in _config.Models)
        {
            if (_backendSelector.Select(modelConfig) == ModelBackendKind.Process)
            {
                _logger.Info("MultiModelHost", $"Model '{modelConfig.Id}' uses the Process backend — not loaded in-process.");
                continue;
            }

            var slot = new ModelSlot(modelConfig.Id, modelConfig, _logger, _rootDir);
            try
            {
                await slot.LoadAsync();
                lock (_slotsLock)
                {
                    _slots[modelConfig.Id] = slot;
                }
            }
            catch (Exception ex)
            {
                slot.Dispose();
                failures.Add($"{modelConfig.Id}: {ex.Message}");
                _logger.Error("MultiModelHost", $"Failed to load model '{modelConfig.Id}': {ex.Message}");
            }
        }

        if (failures.Count > 0 && LoadedCount == 0)
            throw new InvalidOperationException(
                "No models could be loaded: " + string.Join("; ", failures));

        _logger.Info("MultiModelHost", $"Loaded {LoadedCount} model(s): {string.Join(", ", LoadedModelIds)}");
    }

    public ModelSlot GetSlot(string modelId)
    {
        if (!_slots.TryGetValue(modelId, out var slot))
            throw new InvalidOperationException($"Model '{modelId}' not found");
        if (!slot.IsLoaded)
            throw new InvalidOperationException($"Model '{modelId}' not loaded");
        return slot;
    }

    public ModelSlot? TryGetSlot(string modelId)
    {
        if (!_slots.TryGetValue(modelId, out var slot)) return null;
        return slot.IsLoaded ? slot : null;
    }

    public ModelSlot GetMainSlot() => GetSlot(MainModelId);
    public ModelSlot? GetEmbeddingSlot() => EmbeddingModelId != null ? TryGetSlot(EmbeddingModelId) : null;

    public async Task<bool> TryLoadModelAsync(ECAssistant.LLM.Config.ModelConfig modelConfig)
    {
        lock (_slotsLock)
        {
            if (_slots.ContainsKey(modelConfig.Id))
            {
                _logger.Warn("MultiModelHost", $"Model '{modelConfig.Id}' already exists");
                return false;
            }
        }

        var slot = new ModelSlot(modelConfig.Id, modelConfig, _logger, _rootDir);
        try
        {
            await slot.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("MultiModelHost", $"Failed to load model '{modelConfig.Id}': {ex.Message}");
            return false;
        }

        lock (_slotsLock)
        {
            if (_slots.ContainsKey(modelConfig.Id))
            {
                _logger.Warn("MultiModelHost", $"Model '{modelConfig.Id}' already exists");
                slot.Dispose();
                return false;
            }
            _slots.Add(modelConfig.Id, slot);
        }

        _logger.Info("MultiModelHost", $"Loaded new model '{modelConfig.Id}' at runtime");
        return true;
    }

    public bool TryUnloadModel(string modelId)
    {
        lock (_slotsLock)
        {
            if (!_slots.TryGetValue(modelId, out var slot))
                return false;
            slot.Unload();
            _slots.Remove(modelId);
        }
        _logger.Info("MultiModelHost", $"Unloaded model '{modelId}' at runtime");
        return true;
    }

    public IReadOnlyList<ModelInfo> GetModelInfoList()
    {
        List<ModelSlot> snapshot;
        lock (_slotsLock) snapshot = _slots.Values.ToList();
        return snapshot.Select(s => new ModelInfo(
            s.Id, s.Config.Path, s.IsLoaded, s.IsEmbedding,
            s.Config.GpuLayers, s.Config.ContextSize, s.EmbeddingDim
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

public sealed record ModelInfo(
    string Id,
    string Path,
    bool IsLoaded,
    bool IsEmbedding,
    int GpuLayers,
    uint ContextSize,
    int EmbeddingDim
);
