namespace ECAssistant.LLM.Interfaces;

/// <summary>
/// Interface for serializing inference across all clients.
/// </summary>
public interface IInferenceScheduler
{
    int QueueDepth { get; }
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct = default);
}