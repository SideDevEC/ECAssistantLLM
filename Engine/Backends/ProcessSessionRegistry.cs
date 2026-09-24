using System.Collections.Concurrent;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Registry of client sessions on Process-backend models (child llama-server).
/// Mirrors the API and thread-safety contract of <see cref="SessionRegistry"/> for
/// the transcript-backed session type. Sessions are namespaced by clientId.
/// </summary>
public sealed class ProcessSessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, ProcessSession> _sessions = new();
    private readonly IProcessModelHost _host;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly Func<HttpClient> _httpClientFactory;
    private bool _disposed;

    public ProcessSessionRegistry(
        IProcessModelHost host,
        LlmServerConfig config,
        ILogger logger,
        Func<HttpClient>? httpClientFactory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? CreateDefaultHttpClient;
    }

    /// <summary>Number of active process sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Creates a session for a process-backend model. Throws when the session already
    /// exists or the server-wide session cap is reached (same contract as SessionRegistry).
    /// </summary>
    public ProcessSession Create(string clientId, string sessionId, ModelConfig model)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(model);

        var key = $"{clientId}:{sessionId}";
        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Session already exists: {key}");
        if (_sessions.Count >= _config.Server.MaxSessions)
            throw new InvalidOperationException($"Max sessions reached ({_config.Server.MaxSessions})");

        var context = new ProcessSession(
            clientId, sessionId, model, _host, _httpClientFactory(), _logger);

        // Atomic add — prevents two concurrent creates from silently overwriting.
        if (!_sessions.TryAdd(key, context))
        {
            context.Dispose();
            throw new InvalidOperationException($"Session already exists: {key}");
        }

        // No VramBudget reservation: weights + KV cache are owned by the child process.
        _logger.Info("ProcessSessionRegistry", $"Created process session '{key}' on model '{model.Id}'");
        return context;
    }

    /// <summary>Get a session by composite key, or null.</summary>
    public ProcessSession? Get(string clientId, string sessionId)
        => _sessions.GetValueOrDefault($"{clientId}:{sessionId}");

    /// <summary>True when the given (clientId, sessionId) belongs to a process session.</summary>
    public bool Contains(string clientId, string sessionId)
        => _sessions.ContainsKey($"{clientId}:{sessionId}");

    /// <summary>Destroys one session; returns false when it did not exist.</summary>
    public bool Destroy(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        if (!_sessions.TryRemove(key, out var context))
            return false;
        context.Dispose();
        _logger.Info("ProcessSessionRegistry", $"Destroyed process session '{key}'");
        return true;
    }

    /// <summary>Destroys all sessions for a client (disconnect/eviction). Returns the count freed.</summary>
    public int DestroyClient(string clientId)
    {
        var keys = _sessions.Values
            .Where(s => s.ClientId == clientId)
            .Select(s => s.Key)
            .ToList();
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var context))
                context.Dispose();
        }
        if (keys.Count > 0)
            _logger.Info("ProcessSessionRegistry", $"Destroyed {keys.Count} process session(s) for client '{clientId}'");
        return keys.Count;
    }

    /// <summary>Number of active sessions owned by a client (heartbeat accounting).</summary>
    public int CountForClient(string clientId)
        => _sessions.Values.Count(s => s.ClientId == clientId);

       /// <summary>Reset every live process session after a context overflow (safety net).</summary>
    public int ResetAllForOverflow()
         {
            var count = 0;
            foreach (var s in _sessions.Values)
                try { s.Reset(); count++; } catch { }
             if (count > 0)
                  _logger.Warn("ProcessSessionRegistry", $"Reset {count} process session(s) after context overflow.");
            return count;
          }

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = Timeout.InfiniteTimeSpan // streamed inference stays open as long as it needs
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var context in _sessions.Values)
            context.Dispose();
        _sessions.Clear();
    }
}
