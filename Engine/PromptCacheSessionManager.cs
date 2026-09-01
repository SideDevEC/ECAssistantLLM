using LLama.Common;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Owns one <see cref="PromptCacheSession"/> per model slot (lazily created).
/// Stateless/background inference funnels through here so repeated instruction
/// prefixes (summarize/plan/decompose/intent templates) reuse the warm KV cache
/// instead of cold-prefilling on every call.
/// </summary>
public sealed class PromptCacheSessionManager : IDisposable
{
    private readonly MultiModelHost _models;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, PromptCacheSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PromptCacheSessionManager(MultiModelHost models, ILogger logger)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Infer via the cached session for the given slot's model.</summary>
    public async IAsyncEnumerable<string> InferAsync(
        ModelSlot slot,
        string prompt,
        InferenceParams inferenceParams,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PromptCacheSessionManager));
        var session = GetOrCreate(slot);
        await foreach (var token in session.InferAsync(prompt, inferenceParams, ct))
            yield return token;
    }

    /// <summary>Get or lazily create the cache session for a slot's model.</summary>
    public PromptCacheSession GetOrCreate(ModelSlot slot)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(slot.Id, out var existing))
                return existing;

            // Null guard (was slot.Weights!): Weights is null when the slot is not
            // loaded or was unloaded concurrently. Low risk in practice — the only
            // caller (RequestRouter.CreateStatelessStream) is behind the disabled
            // EnableWarmPromptCache flag — but fail loudly instead of poisoning the
            // session with null weights.
            var weights = slot.Weights
                ?? throw new InvalidOperationException(
                    $"Model '{slot.Id}' has no loaded weights — cannot create prompt cache session");

            var session = new PromptCacheSession(slot.Id, weights, slot.Params, _logger);
            _sessions[slot.Id] = session;
            return session;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
                s.Dispose();
            _sessions.Clear();
        }
    }
}
