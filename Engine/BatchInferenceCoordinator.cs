using ECAssistantInference.Abstractions;
using ECAssistantInference.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Coordinates batched inference across all active batch sessions.
/// Uses ECAssistantInference's IConversationPool for native multi-seq decode.
/// </summary>
public sealed class BatchInferenceCoordinator : IDisposable
{
    private readonly IInferenceContext _context;
    private readonly IConversationPool _pool;
    private readonly IInferenceModel? _model;
    private readonly IVisionEncoder? _vision;
    private readonly ILogger _logger;
    private readonly string _modelId;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private bool _disposed;

    private readonly List<BatchSession> _sessions = new();
    private readonly List<BatchSession> _graveyard = new();
    private readonly object _sessionsLock = new();

    public IInferenceContext Context => _context;
    public IInferenceModel? Model => _model;
    public uint PoolSize => _pool.Size;
    public int PoolAvailable => _pool.AvailableCount;

    public BatchInferenceCoordinator(
        IInferenceContext context,
        IConversationPool pool,
        IInferenceModel? model,
        IVisionEncoder? vision,
        ILogger logger,
        string modelId)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _model = model;
        _vision = vision;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));

        _logger.Info("BatchInferenceCoordinator",
            $"[{modelId}] Conversation pool size: {_pool.Size}");
    }

    internal void Register(BatchSession session)
    {
        lock (_sessionsLock)
            _sessions.Add(session);
    }

    internal void Retire(BatchSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
            _graveyard.Add(session);
        }
    }

    public async Task<InferResult> RunInferCycleAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));

        await _cycleGate.WaitAsync(ct);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));
            return await RunInferCycleCoreAsync(ct);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<InferResult> RunInferCycleCoreAsync(CancellationToken ct)
    {
        // Dispose retired conversations inside the gate
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

        List<BatchSession> snapshot;
        lock (_sessionsLock)
            snapshot = _sessions.ToList();

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
            }
        }

        if (anyPromptFlushed)
        {
            var result = _context.InferAll();
            if (result != InferResult.Ok)
            {
                _logger.Warn("BatchInferenceCoordinator",
                    $"[{_modelId}] InferAll returned {result}");
            }
            return result;
        }

        return InferResult.Ok;
    }

    internal IConversation? LeaseConversation()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BatchInferenceCoordinator));
        try
        {
            return _pool.Lease();
        }
        catch (ECAssistantInference.Exceptions.InferenceException)
        {
            return null; // pool exhausted
        }
    }

    internal IVisionEncoder? Vision => _vision;

    public void Dispose()
    {
        if (_disposed) return;
        _cycleGate.Wait();
        try
        {
            _disposed = true;
            _pool.Dispose();
            _context.Dispose();
        }
        finally
        {
            _cycleGate.Release();
        }
    }
}
