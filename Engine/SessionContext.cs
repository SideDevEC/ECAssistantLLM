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

    /// <summary>MTMD projector shared with this model's slot (null = text-only). NOT owned by this context.</summary>
    private readonly MtmdWeights? _mtmd;

    /// <summary>LLamaContext (owns the KV cache). null after reset, recreated on demand.</summary>
    private LLamaContext? _context;
    // Serializes KV-cache mutations (Reset) against streaming reads (Prefill/Infer).
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>Interactive executor with its own KV cache.</summary>
    public InteractiveExecutor? Executor { get; private set; }

    /// <summary>Saved executor state for rewind (bookkeeping counters + token arrays).</summary>
    private LLama.StatefulExecutorBase.ExecutorBaseState? _savedState;

    /// <summary>
    /// Saved NATIVE KV cache snapshot for rewind. Required in addition to
    /// <see cref="_savedState"/>: LLamaSharp 0.27's ExecutorBaseState captures only
    /// executor bookkeeping (n_past counters, token arrays) — NOT the llama.cpp KV
    /// cache. Restoring bookkeeping without the cache leaves the executor's position
    /// past the actual cache contents and the next llama_decode fails with
    /// 'InvalidInputBatch'. Owns unmanaged memory — always disposed on replace/reset.
    /// </summary>
    private LLamaContext.State? _savedKvState;

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

    /// <summary>v14.9: toolset pinning — fingerprint of the tool definitions this
    /// session's KV cache was built with. Set at session create (client-supplied) or
    /// adopted on first tools chat. A mismatch on a later tools request triggers a
    /// deterministic cache reset (tool defs live in the prompt — a stale cache breaks
    /// prefix reuse and pollutes context). Null = not pinned.</summary>
    public string? ToolsHash { get; set; }

    /// <summary>Estimated KV cache memory in MB.</summary>
    public double EstimatedVramMb { get; }

    public SessionContext(
        string clientId,
        string sessionId,
        string modelId,
        LLamaWeights weights,
        ModelParams modelParams,
        InferenceParams inferenceParams,
        ILogger logger,
        MtmdWeights? mtmd = null)
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
        _mtmd = mtmd;

        // Create context + executor (own KV cache)
        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance;
        _context = _weights.CreateContext(_modelParams);
        Executor = CreateExecutor(nullLog);

        EstimatedVramMb = EstimateVramMb();
    }

    private InteractiveExecutor CreateExecutor(Microsoft.Extensions.Logging.ILogger<LLamaContext> nullLog)
        => _mtmd != null ? new InteractiveExecutor(_context!, _mtmd, nullLog) : new InteractiveExecutor(_context!, nullLog);

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

        // Block a concurrent Reset() for the duration of the prefill
        await _ioLock.WaitAsync(ct);
        try
        {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await foreach (var token in Executor.InferAsync(text, _inferenceParams, cts.Token))
            {
                sb.Append(token);
                // INTENTIONAL heuristic stop: break at the first newline — the static
                // prefix is considered "warmed" once a full line has been produced.
                // NOTE the quadratic cost: sb.ToString() re-scans the whole buffer per
                // token (O(n²) over prefill length). Accepted by design: prefill is
                // bounded by the 120s cts below and token counts are modest.
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
        // APPROXIMATE accounting (documented): text.Length/4 chars-per-token is a rough
        // estimate, not an exact tokenizer count. It feeds VRAM budgeting and eviction
        // heuristics only — never generation correctness.
        ApproxTokenCount = EstimateTokenCount(text);

        _logger.Info("SessionContext", $"[{Key}] Prefilled {ApproxTokenCount} tokens in {elapsedMs}ms");
        return (true, ApproxTokenCount, elapsedMs);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Save current state for later rewind: executor bookkeeping AND the native KV
    /// cache. Both snapshots must be taken atomically (under the IO lock) or the two
    /// halves can disagree about n_past and the next decode fails.
    /// </summary>
    public bool SaveState()
    {
        if (Executor == null || _context == null) return false;

        if (!_ioLock.Wait(ResetLockTimeout))
        {
            _logger.Warn("SessionContext", $"[{Key}] SaveState timed out waiting for the IO lock (inference in progress?)");
            return false;
        }

        try
        {
            var state = Executor.GetStateData();
            var kv = _context.GetState();
            // Only commit after both snapshots succeeded — dispose the orphan.
            _savedKvState?.Dispose();
            _savedKvState = kv;
            _savedState = state;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] SaveState failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Rewind to the last saved state: restore the native KV cache first, then the
    /// executor bookkeeping. Without the KV cache half the executor's n_past is left
    /// past the real cache contents and llama_decode fails with 'InvalidInputBatch'.
    /// </summary>
    public async Task<bool> RewindAsync()
    {
        if (Executor == null || _context == null) return false;

        if (_savedState == null || _savedKvState == null)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind: no saved state");
            return false;
        }

        // v-fix: bounded wait — a wedged inference previously blocked Rewind forever
        // (Reset uses the same bounded pattern). Surface as rewound=false instead.
        if (!await _ioLock.WaitAsync(ResetLockTimeout))
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind timed out waiting for the IO lock (inference in progress?)");
            return false;
        }
        try
        {
            // 1. Native KV cache (llama_set_state_data) — must match the bookkeeping below.
            _context.LoadState(_savedKvState);
            // 2. Executor bookkeeping (n_past, consumed counters, token arrays).
            await Executor.LoadState(_savedState);
            // Pending multimodal media cannot survive a rewind — the projector's
            // internal chunk state is not captured by either snapshot.
            try { _mtmd?.ClearMedia(); } catch { }
            _logger.Info("SessionContext", $"[{Key}] Rewound to saved state");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("SessionContext", $"[{Key}] Rewind failed: {ex.Message}");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>How long Reset() waits for an in-flight inference to release the IO lock
    /// before failing fast instead of blocking the caller indefinitely.</summary>
    private static readonly TimeSpan ResetLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reset KV cache completely (destroys context + executor, creates fresh ones).
    /// Caller must re-prefill after this.
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when an in-flight inference holds the
    /// IO lock beyond <see cref="ResetLockTimeout"/> — bounded wait, never indefinite.</exception>
    public void Reset()
    {
        // Bounded lock acquisition: a wedged/hung inference must not block the
        // management thread forever. Callers surface TimeoutException as an error.
        if (!_ioLock.Wait(ResetLockTimeout))
            throw new TimeoutException($"[{Key}] Reset timed out after {ResetLockTimeout.TotalSeconds}s waiting for the IO lock (inference in progress?)");
        try
        {
            var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<LLamaContext>.Instance;

            // Build the replacement first, then swap — inference blocked on _ioLock
            // never observes a null executor or a disposed context.
            var newContext = _weights.CreateContext(_modelParams);
            var oldContext = _context;
            _context = newContext;
            Executor = CreateExecutor(nullLog);
            oldContext?.Dispose();

            _savedState = null;
            _savedKvState?.Dispose();
            _savedKvState = null;
            IsPrefilled = false;
            ApproxTokenCount = 0;
            _logger.Info("SessionContext", $"[{Key}] KV cache reset");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Infer with streaming (token by token).
    /// When images are provided, they are queued into the MTMD projector before the prompt
    /// runs — the prompt must contain one media marker per image, in order.
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(string prompt, InferenceParams? inferenceParams = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<byte[]>? images = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SessionContext));
        if (Executor == null)
            throw new InvalidOperationException("Session executor is null (reset not called?)");
        if (images is { Count: > 0 } && _mtmd == null)
            throw new InvalidOperationException("Model has no mmproj loaded — images are not supported on this session.");

        LastActivity = DateTime.UtcNow;

        var sb = new StringBuilder();
        await _ioLock.WaitAsync(ct);
        try
        {
            // Queue media into the projector so tokenizer consumes them FIFO at each marker.
            if (_mtmd != null && images is { Count: > 0 })
            {
                _mtmd.ClearMedia();
                foreach (var img in images)
                    _mtmd.LoadMedia(img);
            }

            var executor = Executor; // stable reference for the whole stream
            // Substitute the projector's actual marker for the placeholder written by the converter
            var mtmdMarker = MtmdMarkerResolver.GetMarkerFor(executor);
            prompt = prompt.Replace(ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, mtmdMarker);
            if (images is { Count: > 0 })
            {
                var markerCount = 0; var idx = 0;
                while ((idx = prompt.IndexOf(mtmdMarker, idx, StringComparison.Ordinal)) >= 0) { markerCount++; idx += mtmdMarker.Length; }
                _logger.Info("SessionContext", $"Vision: {images.Count} media queued, {markerCount} marker(s) in prompt (prompt len {prompt.Length})");
            }
            await foreach (var token in executor.InferAsync(prompt, inferenceParams ?? _inferenceParams, ct))
            {
                sb.Append(token);
                yield return token;
            }

            // Token accounting must be consistent with the same critical section that
            // guards Reset()/Prefill — otherwise a concurrent reset could wipe or race it.
            ApproxTokenCount += EstimateTokenCount(sb.ToString());
        }
        finally
        {
            try { _mtmd?.ClearMedia(); } catch { }
            _ioLock.Release();
        }
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
        _savedKvState?.Dispose();
        _savedKvState = null;
    }
}