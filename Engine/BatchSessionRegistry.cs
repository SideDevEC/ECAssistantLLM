using System.Collections.Concurrent;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Registry of batch sessions (sub-agent / stateless) when <c>continuous_batching</c>
/// is enabled. Mirrors the <see cref="SessionRegistry"/> API but manages
/// <see cref="BatchSession"/> objects on shared <see cref="BatchedExecutor"/> instances.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public sealed class BatchSessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, BatchSession> _sessions = new();
    private readonly BatchedExecutorHost _host;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private bool _disposed;

    public int Count => _sessions.Count;

    public BatchSessionRegistry(BatchedExecutorHost host, LlmServerConfig config, ILogger logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Create a new batch session on the shared executor.</summary>
    public BatchSession CreateSession(string clientId, string sessionId, string? modelId = null, string? toolsHash = null)
    {
        var key = $"{clientId}:{sessionId}";

        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Batch session already exists: {key}");

        // Determine which model to use
        var coordinator = _host.GetCoordinator(modelId) ?? throw new InvalidOperationException(
            $"No batched executor available for model '{modelId ?? "main"}'");

        // Use the model ID from the coordinator's context if not explicitly provided
        var resolvedModelId = string.IsNullOrEmpty(modelId) ? "main" : modelId;

        var session = new BatchSession(clientId, sessionId, resolvedModelId, coordinator, _logger);

        if (!_sessions.TryAdd(key, session))
        {
            session.Dispose();
            throw new InvalidOperationException($"Batch session already exists: {key}");
        }

        if (!string.IsNullOrEmpty(toolsHash))
            session.ToolsHash = toolsHash;

        _logger.Info("BatchSessionRegistry", $"Created batch session '{key}' on model '{resolvedModelId}'");
        return session;
    }

    public BatchSession? GetSession(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        return _sessions.GetValueOrDefault(key);
    }

    public bool DestroySession(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        if (!_sessions.TryRemove(key, out var session))
            return false;

        // Non-blocking: Dispose only retires the session; the coordinator disposes the
        // conversation inside its cycle gate. A fire-and-forget cleanup cycle makes the
        // KV regions reusable promptly even if no other request arrives.
        session.Dispose();
        KickCleanupCycle(session.Coordinator);
        _logger.Info("BatchSessionRegistry", $"Destroyed batch session '{key}'");
        return true;
    }

    public int DestroyClientSessions(string clientId)
    {
        var keys = _sessions.Where(kvp => kvp.Value.ClientId == clientId).Select(kvp => kvp.Key).ToList();
        var coordinators = new List<BatchInferenceCoordinator>();
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                coordinators.Add(session.Coordinator);
                session.Dispose();
            }
        }
        foreach (var coord in coordinators.Distinct())
            KickCleanupCycle(coord);
        if (keys.Count > 0)
            _logger.Info("BatchSessionRegistry", $"Destroyed {keys.Count} batch session(s) for client '{clientId}'");
        return keys.Count;
    }

    public IReadOnlyList<SessionStatusInfo> ListSessions()
    {
        return _sessions.Values.Select(s => new SessionStatusInfo(
            s.ClientId, s.SessionId, s.ModelId, s.IsPrefilled,
            s.ApproxTokenCount, s.ContextSize, s.EstimatedVramMb,
            s.CreatedAt, s.LastActivity
        )).ToList();
    }

    /// <summary>
    /// Enqueue a Reset on every IDLE batch session and kick a cleanup cycle per affected
    /// coordinator. Sessions actively serving a request are SKIPPED — a live stream must
    /// never be reset out from under its request (each request must feel fully isolated).
    /// Busy sessions own their own overflow handling (ContextOverflowException path).
    /// Non-blocking: cycles run fire-and-forget; every later cycle also applies any
    /// remaining resets, and executor disposal frees everything at shutdown.
    /// </summary>
    public int ResetAllForOverflow()
    {
        var count = 0;
        var coordinators = new List<BatchInferenceCoordinator>();
        foreach (var session in _sessions.Values)
        {
            try
            {
                // Skip busy sessions — never reset out from under a live request
                if (session.IsBusy)
                    continue;
                // Non-blocking enqueue (no sync-over-async); a cycle applies it
                coordinators.Add(session.Coordinator);
                _ = session.ResetAsync();
                count++;
            }
            catch { /* a single bad session must not stop the rest */ }
        }
        foreach (var coord in coordinators.Distinct())
            KickCleanupCycle(coord);
        if (count > 0)
            _logger.Warn("BatchSessionRegistry", $"Reset {count} batch session(s) after context overflow");
        return count;
    }

    /// <summary>Fire-and-forget one coordinator cycle: applies graveyard disposals +
    /// buffered resets. Never observed — errors are swallowed; later cycles retry, and
    /// executor disposal frees everything at shutdown.</summary>
    private void KickCleanupCycle(BatchInferenceCoordinator coordinator)
    {
        _ = Task.Run(async () =>
        {
            try { await coordinator.RunInferCycleAsync(); }
            catch { /* best-effort cleanup; executor disposal is the safety net */ }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        // Executor disposal (BatchedExecutorHost) frees the shared KV pool — no
        // conversation can leak past shutdown.
    }
}