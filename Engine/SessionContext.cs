using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Per-session inference state: own InteractiveExecutor + KV cache.
/// Each ECAssistantCore session maps to one SessionContext on the server.
/// </summary>
public sealed class SessionContext : IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Composite key: {clientId}:{sessionId}.</summary>
    public string Key { get; }

    /// <summary>Client that owns this session.</summary>
    public string ClientId { get; }

    /// <summary>Session ID within the client namespace.</summary>
    public string SessionId { get; }

    /// <summary>Model ID this session uses.</summary>
    public string ModelId { get; }

    /// <summary>Weights used to create this session (for reset).</summary>
    private readonly LLamaWeights _weights;

    /// <summary>Model params used to create this session (for reset).</summary>
    private readonly ModelParams _modelParams;

    /// <summary>Default inference params for this session.</summary>
    private readonly InferenceParams _inferenceParams;

    /// <summary>LLamaContext (owns the KV cache). null after reset, recreated on demand.</summary>
    private LLamaContext? _context;

    /// <summary>Interactive executor with its own KV cache.</summary>
    public InteractiveExecutor? Executor { get; private set; }

    /// <summary>Saved KV cache state for rewind.</summary>
    private LLama.StatefulExecutorBase.ExecutorBaseState? _savedState;

    /// <summary>Whether the static prefix has been prefilled.</summary>
    public bool IsPrefilled { get; private set; }

    /// <summary>Approximate token count in the KV cache.</summary>
    public int ApproxTokenCount { get; private set; }

    /// <summary>Context size (max KV cache capacity).</summary>
    public uint ContextSize { get; }

    /// <summary>When this session was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Last activity time (for eviction).</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    /// <summary>Estimated KV cache memory in MB.</summary>
    public double EstimatedVramMb { get; }

    public SessionContext(
        string clientId,
        string sessionId,
        string modelId,
        LLamaWeights weights,
        ModelParams modelParams,
        InferenceParams inferenceParams,
        ILogger logger)
    {
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        Key = $"{clientId}:{sessionId}";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ContextSize = modelParams.ContextSize ?? 4096;
        _weights = weights;
        _modelParams = modelParams;
        _inferenceParams = inferenceParams;

        // Create context + executor (own KV cache)
        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance;
        _context = _weights.CreateContext(_modelParams);
        Executor = new InteractiveExecutor(_context, nullLog);

        EstimatedVramMb = EstimateVramMb();
    }

    /// <summary>
    /// Prefill the KV cache with a static prefix (system prompt + tools).
    /// </summary>
    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (IsPrefilled)
            return (true, ApproxTokenCount, 0);
        if (Executor == null)
            return (false, 0, 0);

        var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        var sb = new StringBuilder();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await foreach (var token in Executor.InferAsync(text, _inferenceParams, cts.Token))
            {
                sb.Append(token);
                if (sb.ToString().Contains('\n'))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("SessionContext", $"[{Key}] Prefill timed out after 120s");
            return (false, 0, 120000);
        }
        catch (Exception ex)
        {
            _logger.Error("SessionContext", $"[{Key}] Prefill failed: {ex.Message}");
            return (false, 0, 0);
        }

        var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
        IsPrefilled = true;
        ApproxTokenCount = EstimateTokenCount(text);

        _logger.Info("SessionContext", $"[{Key}] Prefilled {ApproxTokenCount} tokens in {elapsedMs}ms");
        return (true, ApproxTokenCount, elapsedMs);
    }

    /// <summary>
    /// Save current KV cache state (for later rewind).
    /// </summary>
    public bool SaveState()
    {
        if (Executor == null) return false;

        try
        {
            _savedState = Executor.GetStateData();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] SaveState failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rewind KV cache to the last saved state.
    /// </summary>
    public async Task<bool> RewindAsync()
    {
        if (Executor == null) return false;

        if (_savedState == null)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind: no saved state");
            return false;
        }

        try
        {
            await Executor.LoadState(_savedState);
            _logger.Info("SessionContext", $"[{Key}] Rewound to saved state");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reset KV cache completely (destroys context + executor, creates fresh ones).
    /// Caller must re-prefill after this.
    /// </summary>
    public void Reset()
    {
        // InteractiveExecutor doesn't implement IDisposable — just drop the reference
        Executor = null;
        _context?.Dispose();

        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance;
        _context = _weights.CreateContext(_modelParams);
        Executor = new InteractiveExecutor(_context, nullLog);

        _savedState = null;
        IsPrefilled = false;
        ApproxTokenCount = 0;
        _logger.Info("SessionContext", $"[{Key}] KV cache reset");
    }

    /// <summary>
    /// Infer with streaming (token by token).
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(string prompt, InferenceParams? inferenceParams = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (Executor == null)
            throw new InvalidOperationException("Session executor is null (reset not called?)");

        LastActivity = DateTime.UtcNow;

        var sb = new StringBuilder();
        await foreach (var token in Executor.InferAsync(prompt, inferenceParams ?? _inferenceParams, ct))
        {
            sb.Append(token);
            yield return token;
        }

        ApproxTokenCount += EstimateTokenCount(sb.ToString());
    }

    /// <summary>
    /// Get the LLamaContext (for tokenization).
    /// </summary>
    public LLamaContext? GetContext() => _context;

    private static int EstimateTokenCount(string text) => text.Length / 4; // rough: ~4 chars/token

    private double EstimateVramMb()
    {
        // Rough: 2 * n_layers * n_ctx * n_embd * sizeof(half) / 1M
        // Approximate for 8B: 28 layers, 4096 dim
        var approxLayers = 28;
        var approxDim = 4096;
        return Math.Round(2.0 * approxLayers * ContextSize * approxDim * 2 / (1024 * 1024), 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // InteractiveExecutor doesn't implement IDisposable
        _context?.Dispose();
    }
}