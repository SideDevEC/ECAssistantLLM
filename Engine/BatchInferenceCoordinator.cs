using LLama;
using LLama.Batched;
using LLama.Native;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Coordinates <see cref="BatchedExecutor.Infer"/> calls across all active batch sessions
/// using a BUFFER system — no locking.
///
/// <para>Sessions never touch their <see cref="Conversation"/> directly. They buffer
/// prompts and mutations (compact, reset, save) in their own local state. The coordinator
/// collects all pending buffers, applies mutations first, flushes prompts, then runs
/// ONE <c>Infer()</c> for all sessions. This means:</para>
///
/// <list type="bullet">
/// <item>Session A can compact while session B is mid-generation — A just marks itself
///   for compaction. The coordinator applies it before the next Infer() cycle.</item>
/// <item>Multiple compactions can happen in one cycle — the coordinator processes them
///   sequentially before Infer().</item>
/// <item>No lock contention — sessions write to their own buffers, coordinator reads them.</item>
/// </list>
///
/// <para>The coordinator is the ONLY thread that touches conversations. Sessions are
/// producers (buffer operations), coordinator is the consumer (apply + Infer).</para>
/// </summary>
public sealed class BatchInferenceCoordinator : IDisposable
{
    private readonly BatchedExecutor _executor;
    private readonly ILogger _logger;
    private readonly string _modelId;
    // Audit fix (2026-09-25): cycles MUST be serialized. Sessions call RunInferCycleAsync
    // from their own request threads; without this gate, concurrent cycles race on
    // TakePendingPrompt (double Prompt → ConversationAlreadyPromptedException) and touch
    // conversations concurrently. The gate serializes mutation+flush+Infer as ONE atomic
    // cycle while still batching ALL pending work per cycle.
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private bool _disposed;

    /// <summary>Registered sessions — the coordinator iterates this to collect buffers.</summary>
    private readonly List<BatchSession> _sessions = new();
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

    /// <summary>Register a session so the coordinator collects its buffers each cycle.</summary>
    internal void Register(BatchSession session)
    {
        lock (_sessionsLock)
            _sessions.Add(session);
    }

    /// <summary>Unregister a session (on dispose).</summary>
    internal void Unregister(BatchSession session)
    {
        lock (_sessionsLock)
            _sessions.Remove(session);
    }

    /// <summary>
    /// Run one Infer cycle — the core of the buffer system (serialized, one at a time):
    /// 1. Collect all pending mutations (compaction, reset, save) from registered sessions
    /// 2. Apply mutations to conversations (dispose+reload, shiftleft, save state)
    /// 3. Flush all pending prompts to conversations
    /// 4. Run ONE Infer() — all conversations with pending tokens batch in one llama_decode
    ///
    /// No locking — the coordinator is the only thread touching conversations.
    /// Sessions buffer their operations and wait for the coordinator to process them.
    /// </summary>
    public async Task<DecodeResult> RunInferCycleAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));

        await _cycleGate.WaitAsync(ct);
        try
        {
            return await RunInferCycleCoreAsync(ct);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<DecodeResult> RunInferCycleCoreAsync(CancellationToken ct)
    {
        // Snapshot sessions under lock — quick, no Infer() delay
        List<BatchSession> snapshot;
        lock (_sessionsLock)
            snapshot = _sessions.ToList();

        // 1. Apply pending mutations (compaction, reset, save) BEFORE flushing prompts
        foreach (var session in snapshot)
        {
            var mutation = session.TakePendingMutation();
            if (mutation == null) continue;

            try
            {
                switch (mutation.Type)
                {
                    case PendingMutationType.Save: session.ApplySave(); break;
                    case PendingMutationType.Rewind: session.ApplyRewind(); break;
                    case PendingMutationType.Reset: session.ApplyReset(); break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("BatchInferenceCoordinator", $"[{session.Key}] {mutation.Type} failed: {ex.Message}");
            }
        }

        // 2. Flush all pending prompts to conversations.
        // Per-session try/catch: one bad prompt (e.g. already-prompted) must not
        // abort the whole cycle for the other sessions.
        foreach (var session in snapshot)
        {
            var pendingPrompt = session.TakePendingPrompt();
            if (pendingPrompt == null) continue;
            try
            {
                var pendingImages = session.TakePendingImages();
                session.ApplyPrompt(pendingPrompt, pendingImages);
            }
            catch (Exception ex)
            {
                _logger.Warn("BatchInferenceCoordinator", $"[{session.Key}] Prompt flush failed: {ex.Message}");
            }
        }

        // 3. Run ONE Infer() for ALL conversations with pending tokens
        var result = await _executor.Infer(ct);

        if (result != DecodeResult.Ok)
        {
            _logger.Warn("BatchInferenceCoordinator",
                $"[{_modelId}] Infer() returned {result} (batchedTokens={_executor.BatchedTokenCount}, queueDepth={_executor.BatchQueueCount})");
        }

        return result;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _executor.Dispose();
    }
}

// ── Pending operation types (buffered by sessions, applied by coordinator) ──

internal enum PendingMutationType
{
    Save,
    Rewind,
    Reset
}

/// <summary>
/// A pending mutation buffered by a <see cref="BatchSession"/>. The coordinator
/// applies these before the next Infer() cycle. Sessions can buffer mutations
/// freely (e.g. compaction) without touching the shared context — the coordinator
/// handles the actual conversation manipulation.
/// </summary>
internal sealed class PendingMutation
{
    public PendingMutationType Type { get; init; }
}