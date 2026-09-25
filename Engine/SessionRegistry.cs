using System.Collections.Concurrent;
using ECAssistantInference.Models;
using InfContextConfig = ECAssistantInference.Models.ContextConfig;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Interfaces;

namespace ECAssistant.LLM.Engine;

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

    public int Count => _sessions.Count;

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

        var defaultSampling = CreateSamplingConfig(_config.Inference);

        var infModelConfig = new ECAssistantInference.Models.ModelConfig
        {
            Path = slot.Config.Path,
            GpuLayers = slot.EffectiveGpuLayers,
            Threads = slot.Config.Threads,
            FlashAttention = slot.Config.FlashAttn,
        };
        var context = new SessionContext(
            clientId, sessionId, slot.Id,
            slot.Model!, infModelConfig, CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers),
            defaultSampling, _logger,
            vision: slot.Vision);

        if (!_sessions.TryAdd(key, context))
        {
            context.Dispose();
            throw new InvalidOperationException($"Session already exists: {key}");
        }

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

    public SessionContext? GetSession(string clientId, string sessionId)
    {
        var key = $"{clientId}:{sessionId}";
        return _sessions.GetValueOrDefault(key);
    }

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

    public IReadOnlyList<SessionStatusInfo> ListSessions()
    {
        return _sessions.Values.Select(s => new SessionStatusInfo(
            s.ClientId, s.SessionId, s.ModelId, s.IsPrefilled,
            s.ApproxTokenCount, s.ContextSize, s.EstimatedVramMb,
            s.CreatedAt, s.LastActivity
        )).ToList();
    }

    public int ResetAllForOverflow()
    {
        var count = 0;
        foreach (var session in _sessions.Values)
        {
            try
            {
                session.Reset();
                count++;
            }
            catch { }
        }
        if (count > 0)
            _logger.Warn("SessionRegistry", $"Reset {count} session(s) after context overflow — next request re-prefills.");
        return count;
    }

    internal static ECAssistantInference.Models.ContextConfig CreateCtxConfig(ECAssistant.LLM.Config.ModelConfig config, int effectiveGpuLayers)
    {
        var pooling = config.IsEmbedding
            ? config.PoolingType?.ToLowerInvariant() switch
            {
                "mean" => PoolingType.Mean,
                "cls" => PoolingType.Cls,
                "last" => PoolingType.Last,
                _ => PoolingType.Mean,
            }
            : PoolingType.None;

        return new ECAssistantInference.Models.ContextConfig
        {
            ContextSize = (uint)config.ContextSize,
            BatchSize = config.BatchSize > 0 ? (uint)config.BatchSize : 512,
            SeqMax = 2,
            PoolingType = pooling,
        };
    }

    internal static SamplingConfig CreateSamplingConfig(InferenceDefaults defaults) => new()
    {
        Temperature = defaults.Temperature,
        TopP = defaults.TopP,
        TopK = defaults.TopK,
        RepeatPenalty = defaults.RepeatPenalty,
        MaxTokens = defaults.MaxTokens,
    };

    public void Dispose()
    {
        foreach (var context in _sessions.Values)
            context.Dispose();
        _sessions.Clear();
    }
}

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
