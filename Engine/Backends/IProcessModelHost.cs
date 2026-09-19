using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Abstraction over the externally-served (subprocess) model host. DI seam for tests.
/// </summary>
public interface IProcessModelHost
{
    /// <summary>All currently-running process instances.</summary>
    IReadOnlyList<ProcessModelInstance> Instances { get; }

    /// <summary>
    /// Ensures the runtime binary and model weights exist (downloading when allowed),
    /// then starts a llama-server subprocess for the model. Returns the instance.
    /// </summary>
    Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default);

    /// <summary>
    /// Ensures the model's child llama-server is running and returns its base URL.
    /// Convenience for callers that only need the endpoint (e.g. ProcessSession).
    /// </summary>
    Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default);

    /// <summary>Stops all running instances.</summary>
    Task StopAllAsync();
}
