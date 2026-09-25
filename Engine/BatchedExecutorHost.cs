using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Owns <see cref="BatchedExecutor"/> instances — one per loaded in-process model —
/// when <c>continuous_batching</c> is enabled in server config. Created at startup
/// ONLY when the flag is on; null otherwise (zero overhead when disabled).
/// Each <see cref="BatchedExecutor"/> shares the same <see cref="LLamaWeights"/> as the
/// corresponding <see cref="ModelSlot"/> but creates its own <see cref="LLamaContext"/>
/// for the shared KV pool. Weights are loaded once; the batched context is additional.
/// </summary>
public sealed class BatchedExecutorHost : IDisposable
{
    private readonly Dictionary<string, BatchInferenceCoordinator> _coordinators = new(StringComparer.OrdinalIgnoreCase);
    private readonly MultiModelHost _modelHost;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Estimated VRAM for all shared batched contexts (one per model).</summary>
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
        var batchSize = (int)_config.Server.BatchContextSize;
        if (batchSize <= 0)
            batchSize = 32768;

        foreach (var modelId in _modelHost.LoadedModelIds)
        {
            var slot = _modelHost.TryGetSlot(modelId);
            if (slot == null || slot.Weights == null || slot.IsEmbedding)
                continue;

            // Process-backend models are not eligible — they don't have in-process weights.
            if (slot.Config.Backend.Equals("process", StringComparison.OrdinalIgnoreCase))
                continue;

            var contextParams = new ModelParams(slot.Config.Path)
            {
                ContextSize = (uint)batchSize,
                GpuLayerCount = slot.EffectiveGpuLayers,
                Threads = slot.Config.Threads == -1 ? null : slot.Config.Threads,
                // Physical batch (n_batch / ubatch): keep small — this is the per-decode
                // ubatch, NOT the shared KV pool size. ContextSize above carries the pool.
                BatchSize = 512,
                FlashAttention = slot.Config.FlashAttn,
            };

            // KV cache quantization parity with ModelSlot
            if (!slot.Config.IsEmbedding && !string.Equals(slot.Config.KvCache, "f16", StringComparison.OrdinalIgnoreCase))
            {
                contextParams.TypeK = GGMLType.GGML_TYPE_Q8_0;
                contextParams.TypeV = GGMLType.GGML_TYPE_Q8_0;
            }

            var executor = new BatchedExecutor(slot.Weights, contextParams, slot.Mmproj);
            var coordinator = new BatchInferenceCoordinator(executor, _logger, modelId);
            _coordinators[modelId] = coordinator;

            // Rough VRAM estimate for the shared context
            EstimatedSharedVramMb += EstimateContextVramMb(batchSize);

            _logger.Info("BatchedExecutorHost",
                $"Created BatchedExecutor for model '{modelId}' (shared ctx={batchSize}, gpu_layers={slot.EffectiveGpuLayers})");
        }

        _logger.Info("BatchedExecutorHost",
            $"Initialized {_coordinators.Count} batched executor(s), estimated shared VRAM ~{EstimatedSharedVramMb:F0} MB");
    }

    /// <summary>Get the coordinator for a specific model. Falls back to the main model.</summary>
    public BatchInferenceCoordinator? GetCoordinator(string? modelId = null)
    {
        if (_disposed) return null;

        if (!string.IsNullOrEmpty(modelId) && _coordinators.TryGetValue(modelId, out var coord))
            return coord;

        // Fall back to main model
        var mainId = _modelHost.MainModelId;
        return _coordinators.GetValueOrDefault(mainId);
    }

    private static double EstimateContextVramMb(int contextSize)
    {
        // Rough: 2 * n_layers * n_ctx * n_embd * sizeof(half) / 1M
        // Approximate for 8B: 28 layers, 4096 dim
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