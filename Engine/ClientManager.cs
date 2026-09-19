using System.Collections.Concurrent;
using ECAssistant.LLM.Interfaces;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Manages client connections: registration, heartbeat, eviction.
/// When the last client disconnects and ShutdownOnLastClient is true,
/// triggers the server shutdown callback.
/// </summary>
public sealed class ClientManager : IClientManager, IDisposable
{
    private readonly ConcurrentDictionary<string, ClientRecord> _clients = new();
    private readonly SessionRegistry _sessionRegistry;
    private readonly Engine.Backends.ProcessSessionRegistry? _processSessionRegistry;
    private readonly ILogger _logger;
    private readonly int _heartbeatTimeoutSec;
    private readonly Timer _evictionTimer;
    private readonly bool _shutdownOnLastClient;
    private readonly int _shutdownGraceSec;
    private readonly Action? _onLastClientDisconnected;
    private Timer? _graceTimer; // one-shot countdown before an actual last-client shutdown
    private int _graceActive; // 0 = idle, 1 = countdown running
    private int _everHadClients; // 0 = no clients ever registered, 1 = at least one has
    private int _shutdownTriggered; // 0 = not yet, 1 = shutdown callback fired (guard against 90 s re-trigger spam)

    public ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger)
        : this(sessionRegistry, config, logger, onLastClientDisconnected: null)
    {
    }

    public ClientManager(
        SessionRegistry sessionRegistry,
        ECAssistant.LLM.Config.LlmServerConfig config,
        ILogger logger,
        Action? onLastClientDisconnected)
        : this(sessionRegistry, config, logger, onLastClientDisconnected, processSessionRegistry: null)
    {
    }

    public ClientManager(
        SessionRegistry sessionRegistry,
        ECAssistant.LLM.Config.LlmServerConfig config,
        ILogger logger,
        Action? onLastClientDisconnected,
        Engine.Backends.ProcessSessionRegistry? processSessionRegistry)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _processSessionRegistry = processSessionRegistry;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _heartbeatTimeoutSec = config.Server.HeartbeatTimeoutSec;
        _shutdownOnLastClient = config.Server.ShutdownOnLastClient;
        _shutdownGraceSec = config.Server.ShutdownGraceSec;
        _onLastClientDisconnected = onLastClientDisconnected;

        _evictionTimer = new Timer(EvictStaleClients, null,
            TimeSpan.FromSeconds(_heartbeatTimeoutSec),
            TimeSpan.FromSeconds(_heartbeatTimeoutSec));
    }

    /// <summary>Registered client count.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Register a new client. Returns client ID (UUID).
    /// </summary>
    public string Register(string clientName, string? version = null)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var record = new ClientRecord(clientId, clientName, version ?? "unknown", DateTime.UtcNow);
        _clients[clientId] = record;
        Interlocked.Exchange(ref _everHadClients, 1);
        CancelGraceCountdown($"client '{clientName}' registered");
        _logger.Info("ClientManager", $"Registered client '{clientName}' v{version} → {clientId}");
        return clientId;
    }

    /// <summary>
    /// Record a heartbeat for a client.
    /// </summary>
    public bool Heartbeat(string clientId, int activeSessions)
    {
        if (!_clients.TryGetValue(clientId, out var record))
            return false;

        record.LastHeartbeat = DateTime.UtcNow;
        record.ActiveSessions = activeSessions;
        return true;
    }

    /// <summary>
    /// Disconnect a client (frees all its sessions).
    /// If this was the last client and ShutdownOnLastClient is true,
    /// triggers the server shutdown callback.
    /// </summary>
    public bool Disconnect(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var record))
            return false;

        var freed = _sessionRegistry.DestroyClientSessions(clientId);
        freed += _processSessionRegistry?.DestroyClient(clientId) ?? 0;
        _logger.Info("ClientManager", $"Disconnected client '{record.Name}' ({clientId}), freed {freed} session(s)");

        // Check if this was the last client
        if (_clients.IsEmpty && _shutdownOnLastClient)
        {
            StartGraceCountdown("last client disconnected");
        }

        return true;
    }

    /// <summary>
    /// Check if a client is known and alive.
    /// </summary>
    public bool IsValid(string clientId)
    {
        return _clients.ContainsKey(clientId);
    }

    private void EvictStaleClients(object? state)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_heartbeatTimeoutSec);
        var stale = _clients.Where(kvp => kvp.Value.LastHeartbeat < cutoff).ToList();

        foreach (var kvp in stale)
        {
            if (_clients.TryRemove(kvp.Key, out var record))
            {
                var freed = _sessionRegistry.DestroyClientSessions(kvp.Key);
                freed += _processSessionRegistry?.DestroyClient(kvp.Key) ?? 0;
                _logger.Warn("ClientManager",
                    $"Evicted stale client '{record.Name}' ({kvp.Key}) — " +
                    $"last heartbeat {record.LastHeartbeat:HH:mm:ss}, freed {freed} session(s)");
            }
        }

        // Also check: all clients evicted, should we shut down? Fire once — cts.Cancel()
        // is idempotent, but re-invoking every eviction tick re-logged the shutdown and
        // hid the (now fixed) stuck-shutdown bug behind 854 identical log lines.
        if (_clients.IsEmpty && _shutdownOnLastClient && Interlocked.CompareExchange(ref _everHadClients, 0, 0) == 1)
        {
            StartGraceCountdown("all clients evicted");
        }
    }

    /// <summary>
    /// Begin the shutdown countdown. The callback fires only if no client registers
    /// within the grace window — a reconnect cycle (register → disconnect → register)
    /// must not kill the server. Grace 0 = immediate (legacy behavior).
    /// </summary>
    private void StartGraceCountdown(string reason)
    {
        if (_shutdownGraceSec <= 0)
        {
            if (Interlocked.Exchange(ref _shutdownTriggered, 1) == 1) return;
            _logger.Info("ClientManager", $"{reason} — triggering server shutdown (no grace)");
            _onLastClientDisconnected?.Invoke();
            return;
        }

        if (Interlocked.CompareExchange(ref _graceActive, 1, 0) == 1)
            return; // countdown already running

        _logger.Info("ClientManager",
            $"{reason} — server shuts down in {_shutdownGraceSec}s unless a client returns");
        // Race-safe: create the timer BEFORE publishing it, so a concurrent cancel
        // always sees either the old value or the fully-initialized new one.
        var timer = new Timer(GraceElapsed, null,
            TimeSpan.FromSeconds(_shutdownGraceSec), Timeout.InfiniteTimeSpan);
        var previous = Interlocked.Exchange(ref _graceTimer, timer);
        previous?.Dispose();
    }

    private void GraceElapsed(object? state)
    {
        CancelGraceCountdown(countdownExpired: true);

        if (!_clients.IsEmpty || !_shutdownOnLastClient) return; // a client returned in time

        if (Interlocked.Exchange(ref _shutdownTriggered, 1) == 1) return;
        _logger.Info("ClientManager", "Shutdown grace expired with no clients — triggering server shutdown");
        _onLastClientDisconnected?.Invoke();
    }

    private void CancelGraceCountdown(string? reason = null, bool countdownExpired = false)
    {
        if (Interlocked.Exchange(ref _graceActive, 0) == 1)
        {
            // Take ownership of the timer field — a concurrent cycle may have already
            // replaced it; disposing the exchanged value never touches the new timer.
            var timer = Interlocked.Exchange(ref _graceTimer, null);
            timer?.Dispose();
            if (!countdownExpired)
            {
                _logger.Info("ClientManager", $"Pending shutdown cancelled — {reason}");
                Interlocked.Exchange(ref _shutdownTriggered, 0); // allow a future shutdown cycle
            }
        }
    }

    public void Dispose()
    {
        _evictionTimer?.Dispose();
        _graceTimer?.Dispose();
    }
}

/// <summary>Internal client record.</summary>
internal sealed class ClientRecord
{
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public DateTime RegisteredAt { get; }
    public DateTime LastHeartbeat { get; set; }
    public int ActiveSessions { get; set; }

    public ClientRecord(string id, string name, string version, DateTime registeredAt)
    {
        Id = id;
        Name = name;
        Version = version;
        RegisteredAt = registeredAt;
        LastHeartbeat = registeredAt;
        ActiveSessions = 0;
    }
}

/// <summary>Read-only client info for API responses.</summary>
public sealed record ClientInfo(
    string Id,
    string Name,
    string Version,
    DateTime RegisteredAt,
    DateTime LastHeartbeat,
    int ActiveSessions
);