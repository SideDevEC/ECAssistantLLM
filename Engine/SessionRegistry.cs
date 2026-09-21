using System.Collections.Concurrent;
using LLama;
using LLama.Common;
using LLama.Sampling;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Interfaces;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Registry of all client sessions across the server.
/// Sessions are namespaced by clientId. Thread-safe.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    private readonly MultiModelHost _modelHost;
    private readonly IInferenceScheduler _scheduler;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly VramBudget _vram;

    public SessionRegistry(MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger)
        : this(modelHost, scheduler, config, logger, new VramBudget(config))
    {
    }

    public SessionRegistry(MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger, VramBudget? vram)
    {
        _modelHost = modelHost ?? throw new ArgumentNullException(nameof(modelHost));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _vram = vram ?? throw new ArgumentNullException(nameof(vram));
    }

    /// <summary>Number of active sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Create a new session with its own KV cache.
    /// </summary>
    public SessionContext CreateSession(string clientId, string sessionId, string? modelId = null, string? toolsHash = null)
    {
        var key = $"{clientId}:{sessionId}";

        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Session already exists: {key}");

        if (_sessions.Count >= _config.Server.MaxSessions)
            throw new InvalidOperationException($"Max sessions reached ({_config.Server.MaxSessions})");

        var slot = modelId != null
            ? _modelHost.GetSlot(modelId)
            : _modelHost.GetMainSlot();

        var inferenceParams = CreateInferenceParams(_config.Inference);

        var context = new SessionContext(
            clientId, sessionId, slot.Id,
            slot.Weights!, slot.Params, inferenceParams, _logger,
            mtmd: slot.Mmproj);

        // Atomic add — prevents two concurrent creates from silently overwriting (leaking KV cache)
        if (!_sessions.TryAdd(key, context))
        {
            context.Dispose();
            throw new InvalidOperationException($"Session already exists: {key}");
        }

        // Reserve VRAM here so every destroy/release path stays symmetric
        if (!_vram.TryReserve(context.EstimatedVramMb))
        {
            _sessions.TryRemove(key, out _);
            context.Dispose();
            throw new InvalidOperationException($"VRAM budget exceeded (would need {context.EstimatedVramMb:F0} MB, {_vram.CurrentUsageMb:F0} MB in use of {_vram.MaxMb} MB budget)");
        }

        if (!string.IsNullOrEmpty(toolsHash))
            context.ToolsHash = toolsHash;

        _logger.Info("SessionRegistry", $"Created session '{key}' on model '{slot.Id}'");
        return context;
    }

    /// <summary>
    /// Get a session by composite key.
    /// </summary>
    public SessionContext? GetSession(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        return _sessions.GetValueOrDefault(key);
    }

    /// <summary>
    /// Destroy a session (frees KV cache).
    /// </summary>
    public bool DestroySession(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        if (!_sessions.TryRemove(key, out var context))
            return false;

        _vram.Release(context.EstimatedVramMb);
        context.Dispose();
        _logger.Info("SessionRegistry", $"Destroyed session '{key}'");
        return true;
    }

    /// <summary>
    /// Destroy all sessions for a client (on disconnect/eviction).
    /// </summary>
    public int DestroyClientSessions(string clientId)
    {
        var keys = _sessions.Where(kvp => kvp.Value.ClientId == clientId).Select(kvp => kvp.Key).ToList();
        foreach (var key in keys)
        {
            if (_sessions.TryRemove(key, out var context))
            {
                _vram.Release(context.EstimatedVramMb);
                context.Dispose();
            }
        }
        _logger.Info("SessionRegistry", $"Destroyed {keys.Count} session(s) for client '{clientId}'");
        return keys.Count;
    }

    /// <summary>
    /// List all sessions (for status/diagnostics).
    /// </summary>
    public IReadOnlyList<SessionStatusInfo> ListSessions()
    {
        return _sessions.Values.Select(s => new SessionStatusInfo(
            s.ClientId, s.SessionId, s.ModelId, s.IsPrefilled,
            s.ApproxTokenCount, s.ContextSize, s.EstimatedVramMb,
            s.CreatedAt, s.LastActivity
        )).ToList();
    }

    private static InferenceParams CreateInferenceParams(InferenceDefaults defaults) => new()
    {
        MaxTokens = defaults.MaxTokens,
        AntiPrompts = new[] { "</s>", "User:", "### User" },
        OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
        SamplingPipeline = new DefaultSamplingPipeline
        {
            Temperature = defaults.Temperature,
            TopP = defaults.TopP,
            TopK = defaults.TopK,
            RepeatPenalty = defaults.RepeatPenalty
        }
    };

    public void Dispose()
    {
        foreach (var context in _sessions.Values)
            context.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// Read-only session status for API responses.
/// </summary>
public sealed record SessionStatusInfo(
    string ClientId,
    string SessionId,
    string ModelId,
    bool IsPrefilled,
    int ApproxTokenCount,
    uint ContextSize,
    double EstimatedVramMb,
    DateTime CreatedAt,
    DateTime LastActivity
);