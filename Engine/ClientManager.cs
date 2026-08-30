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
    private readonly ILogger _logger;
    private readonly int _heartbeatTimeoutSec;
    private readonly Timer _evictionTimer;
    private readonly bool _shutdownOnLastClient;
    private readonly Action? _onLastClientDisconnected;

    public ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger)
        : this(sessionRegistry, config, logger, onLastClientDisconnected: null)
    {
    }

    public ClientManager(
        SessionRegistry sessionRegistry,
        ECAssistant.LLM.Config.LlmServerConfig config,
        ILogger logger,
        Action? onLastClientDisconnected)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _heartbeatTimeoutSec = config.Server.HeartbeatTimeoutSec;
        _shutdownOnLastClient = config.Server.ShutdownOnLastClient;
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
        _logger.Info("ClientManager", $"Disconnected client '{record.Name}' ({clientId}), freed {freed} session(s)");

        // Check if this was the last client
        if (_clients.IsEmpty && _shutdownOnLastClient)
        {
            _logger.Info("ClientManager", "Last client disconnected — triggering server shutdown");
            _onLastClientDisconnected?.Invoke();
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
                _logger.Warn("ClientManager",
                    $"Evicted stale client '{record.Name}' ({kvp.Key}) — " +
                    $"last heartbeat {record.LastHeartbeat:HH:mm:ss}, freed {freed} session(s)");
            }
        }

        // Also check: all clients evicted, should we shut down?
        if (_clients.IsEmpty && _shutdownOnLastClient)
        {
            _logger.Info("ClientManager", "All clients evicted — triggering server shutdown");
            _onLastClientDisconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        _evictionTimer?.Dispose();
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