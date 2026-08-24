using System.Collections.Concurrent;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Manages client connections: registration, heartbeat, eviction.
/// </summary>
public sealed class ClientManager
{
    private readonly ConcurrentDictionary<string, ClientRecord> _clients = new();
    private readonly SessionRegistry _sessionRegistry;
    private readonly ILogger _logger;
    private readonly int _heartbeatTimeoutSec;
    private readonly Timer _evictionTimer;

    public ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _heartbeatTimeoutSec = config.Server.HeartbeatTimeoutSec;

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
    /// </summary>
    public bool Disconnect(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var record))
            return false;

        var freed = _sessionRegistry.DestroyClientSessions(clientId);
        _logger.Info("ClientManager", $"Disconnected client '{record.Name}' ({clientId}), freed {freed} session(s)");
        return true;
    }

    /// <summary>
    /// Check if a client is known and alive.
    /// </summary>
    public bool IsValid(string clientId)
    {
        return _clients.ContainsKey(clientId);
    }

    /// <summary>
    /// List all clients.
    /// </summary>
    public IReadOnlyList<ClientInfo> ListClients()
    {
        return _clients.Values.Select(r => new ClientInfo(
            r.Id, r.Name, r.Version, r.RegisteredAt, r.LastHeartbeat, r.ActiveSessions
        )).ToList();
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