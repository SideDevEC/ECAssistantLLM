using ECAssistantInference.Abstractions;
using ECAssistantInference.Models;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Owns BatchInferenceCoordinator instances — one per loaded in-process model —
/// when continuous_batching is enabled. Created at startup ONLY when the flag is on.
/// </summary>
public sealed class BatchedExecutorHost : IDisposable
{
    private readonly Dictionary<string, BatchInferenceCoordinator> _coordinators = new(StringComparer.OrdinalIgnoreCase);
    private readonly MultiModelHost _modelHost;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private bool _disposed;

    public double EstimatedSharedVramMb { get; private set; }

    public BatchedExecutorHost(MultiModelHost modelHost, LlmServerConfig config, ILogger logger)
    {
        _modelHost = modelHost ?? throw new ArgumentNullException(nameof(modelHost));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Initialize();
    }

    private void Initialize()
    {
        foreach (var modelId in _modelHost.LoadedModelIds)
        {
            var slot = _modelHost.TryGetSlot(modelId);
            if (slot == null || slot.Model == null || slot.IsEmbedding)
                continue;

            if (slot.Config.Backend.Equals("process", StringComparison.OrdinalIgnoreCase))
                continue;

            var seqMax = Math.Max(2u, (uint)_config.Server.MaxSessions);

            var ctxConfig = SessionRegistry.CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers);
            // Override SeqMax for batched context
            var batchCtxConfig = new ECAssistantInference.Models.ContextConfig
            {
                ContextSize = ctxConfig.ContextSize,
                BatchSize = ctxConfig.BatchSize,
                SeqMax = seqMax,
                PoolingType = ctxConfig.PoolingType,
            };

            var context = slot.Model.CreateContext(batchCtxConfig);
            var pool = context.CreatePool();
            var coordinator = new BatchInferenceCoordinator(context, pool, slot.Model, slot.Vision, _logger, modelId);
            _coordinators[modelId] = coordinator;

            EstimatedSharedVramMb += EstimateContextVramMb((int)slot.Config.ContextSize);

            _logger.Info("BatchedExecutorHost",
                $"Created batched context for model '{modelId}' (shared ctx={slot.Config.ContextSize}, seq_max={seqMax})");
        }

        _logger.Info("BatchedExecutorHost",
            $"Initialized {_coordinators.Count} batched executor(s), estimated shared VRAM ~{EstimatedSharedVramMb:F0} MB");
    }

    public BatchInferenceCoordinator? GetCoordinator(string? modelId = null)
    {
        if (_disposed) return null;
        if (!string.IsNullOrEmpty(modelId) && _coordinators.TryGetValue(modelId, out var coord))
            return coord;
        return _coordinators.GetValueOrDefault(_modelHost.MainModelId);
    }

    private static double EstimateContextVramMb(int contextSize)
    {
        return Math.Round(2.0 * 28 * contextSize * 4096 * 2 / (1024.0 * 1024.0), 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var coord in _coordinators.Values)
            coord.Dispose();
        _coordinators.Clear();
    }
}
