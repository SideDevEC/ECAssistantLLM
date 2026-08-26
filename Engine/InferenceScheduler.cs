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

        await _gate.WaitAsync(ct);
        Interlocked.Decrement(ref _queueDepth);

        return new InferenceReleaser(_gate);
    }

    private sealed class InferenceReleaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;

        public InferenceReleaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}