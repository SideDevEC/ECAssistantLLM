using ECAssistant.LLM.Interfaces;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Serializes inference across all clients.
/// One model = one inference at a time. FIFO queue with semaphore.
/// </summary>
public sealed class InferenceScheduler : IInferenceScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private int _queueDepth;

    /// <summary>Current queue depth (waiting + executing).</summary>
    public int QueueDepth => _queueDepth;

    public InferenceScheduler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Acquire inference slot. Blocks until it's this client's turn.
    /// Returns a disposable that releases the slot when done.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _queueDepth);
        _logger.Debug("Scheduler", $"Inference queue depth: {_queueDepth}");

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref _queueDepth);
            throw;
        }

        // Keep counting this request as "in queue" until it finishes executing —
        // QueueDepth is documented as waiting + executing.
        return new InferenceReleaser(_gate, () => Interlocked.Decrement(ref _queueDepth));
    }

    private sealed class InferenceReleaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly Action _onRelease;

        public InferenceReleaser(SemaphoreSlim gate, Action onRelease)
        {
            _gate = gate;
            _onRelease = onRelease;
        }

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            _onRelease();
            return ValueTask.CompletedTask;
        }
    }
}