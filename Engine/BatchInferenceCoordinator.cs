using LLama;
using LLama.Batched;
using LLama.Native;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Coordinates <see cref="BatchedExecutor.Infer"/> calls across all active batch sessions.
///
/// <para><b>Buffer system, concurrency-hardened (2026-09-25 second pass):</b>
/// Sessions never touch their <see cref="Conversation"/> directly. Requests enqueue
/// operations into a thread-safe per-session <see cref="BatchOpBuffer"/> (atomic FIFO —
/// nothing can be lost under concurrent writers). The coordinator dequeues each
/// session's ops in FIFO order, applies mutations first-come-first-served, flushes
/// prompts, then runs ONE <c>Infer()</c> for all sessions with pending work.</para>
///
/// <para>Each cycle is serialized by <see cref="_cycleGate"/>: sessions call
/// <c>RunInferCycleAsync</c> from their own request threads, and the gate makes
/// drain + apply + Infer ONE atomic step while still batching ALL pending work per
/// cycle. Different sessions' requests still batch together in one decode.</para>
///
/// <para><b>Deferred disposal:</b> retired sessions (<see cref="Retire"/>) move to a
/// graveyard; their conversations are disposed at the START of the next cycle — inside
/// the gate — so a disconnect racing an in-flight Infer can never dispose a conversation
/// the coordinator is touching.</para>
/// </summary>
public sealed class BatchInferenceCoordinator : IDisposable
{
    private readonly BatchedExecutor _executor;
    private readonly ILogger _logger;
    private readonly string _modelId;
    // Serializes each cycle: mutations+flush+Infer atomic (audit fix 2026-09-25).
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private bool _disposed;

    /// <summary>Registered (live) sessions — the coordinator iterates this to drain buffers.</summary>
    private readonly List<BatchSession> _sessions = new();
    /// <summary>Retired sessions awaiting conversation disposal inside the cycle gate.</summary>
    private readonly List<BatchSession> _graveyard = new();
    private readonly object _sessionsLock = new();

    /// <summary>The underlying executor.</summary>
    public BatchedExecutor Executor => _executor;

    public int BatchedTokenCount => _executor.BatchedTokenCount;
    public int BatchQueueCount => _executor.BatchQueueCount;

    public BatchInferenceCoordinator(BatchedExecutor executor, ILogger logger, string modelId)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
    }

    /// <summary>Register a session so the coordinator drains its op buffer each cycle.</summary>
    internal void Register(BatchSession session)
    {
        lock (_sessionsLock)
            _sessions.Add(session);
    }

    /// <summary>
    /// Retire a session (on Dispose / disconnect). Non-blocking: the conversation is NOT
    /// disposed here — the coordinator disposes graveyard sessions inside the cycle gate,
    /// eliminating the dispose-vs-in-flight-cycle native crash race.
    /// </summary>
    internal void Retire(BatchSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
            _graveyard.Add(session);
        }
    }

    /// <summary>
    /// Run one Infer cycle (serialized, one at a time):
    /// 1. Dispose graveyard sessions' conversations (inside the gate — safe)
    /// 2. Drain every registered session's op buffer in FIFO order (mutations, prompts w/ media)
    /// 3. Run ONE Infer() when any prompt was flushed or the executor has pending tokens
    ///
    /// Per-session try/catch: one bad op must not abort the cycle for other sessions;
    /// the failure is recorded on the session so its requesting call can report it.
    /// </summary>
    public async Task<DecodeResult> RunInferCycleAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));

        await _cycleGate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring: shutdown may have completed while we waited.
            if (_disposed)
                throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));
            return await RunInferCycleCoreAsync(ct);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<DecodeResult> RunInferCycleCoreAsync(CancellationToken ct)
    {
        // 0. Dispose retired conversations — INSIDE the gate, so no in-flight Infer can
        //    race the native dispose (fixes the dispose-vs-cycle crash race).
        List<BatchSession> dead;
        lock (_sessionsLock)
        {
            dead = new List<BatchSession>(_graveyard);
            _graveyard.Clear();
        }
        foreach (var session in dead)
        {
            try { session.DisposeConversationNow(); }
            catch (Exception ex)
            {
                _logger.Warn("BatchInferenceCoordinator", $"[{session.Key}] retire-dispose failed: {ex.Message}");
            }
        }

        // Snapshot live sessions under lock — quick, no Infer() delay
        List<BatchSession> snapshot;
        lock (_sessionsLock)
            snapshot = _sessions.ToList();

        // 1. Drain each session's op buffer in FIFO order. FIFO preserves request
        //    ordering: a Save enqueued before a Reset is applied before it — nothing
        //    is dropped, and concurrent writers can never overwrite each other.
        var anyPromptFlushed = false;
        foreach (var session in snapshot)
        {
            while (session.TryDequeueOp(out var op))
            {
                var ok = false;
                try
                {
                    ok = session.ApplyOp(op);
                }
                catch (Exception ex)
                {
                    _logger.Warn("BatchInferenceCoordinator", $"[{session.Key}] {op.Type} failed: {ex.Message}");
                    continue;
                }

                if (ok && op.Type == PendingMutationType.Prompt)
                    anyPromptFlushed = true;
                else if (!ok)
                    _logger.Warn("BatchInferenceCoordinator", $"[{session.Key}] {op.Type} failed (see session error)");
            }
        }

        // 2. Run ONE Infer() when there is actual decode work: a flushed prompt, or
        //    tokens already queued on the executor. Mutation-only cycles (Save/Rewind/
        //    Reset) need no decode — skipping the Infer keeps those paths cheap.
        if (anyPromptFlushed || _executor.BatchedTokenCount > 0 || _executor.BatchQueueCount > 0)
        {
            var result = await _executor.Infer(ct);

            if (result != DecodeResult.Ok)
            {
                _logger.Warn("BatchInferenceCoordinator",
                    $"[{_modelId}] Infer() returned {result} (batchedTokens={_executor.BatchedTokenCount}, queueDepth={_executor.BatchQueueCount})");
            }

            return result;
        }

        return DecodeResult.Ok;
    }

    /// <summary>Create a new conversation on the shared executor.</summary>
    public Conversation CreateConversation()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));
        return _executor.Create();
    }

    /// <summary>The shared context (for tokenization/detokenization).</summary>
    public LLamaContext Context => _executor.Context;

    /// <summary>
    /// Graceful shutdown: acquire the cycle gate FIRST, so any in-flight native
    /// <c>llama_decode</c> fully completes before the executor is disposed. Disposing the
    /// executor under a running decode is a native crash (same class as the
    /// dispose-vs-cycle race). Request threads racing the flag are serialized behind the
    /// same gate: they either complete their cycle or get ObjectDisposedException after
    /// it — never a native dispose during decode.
    /// Blocking only at process shutdown; normal operation is unaffected.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _cycleGate.Wait();
        try
        {
            _disposed = true;
            _executor.Dispose();
        }
        finally
        {
            _cycleGate.Release();
        }
    }
}