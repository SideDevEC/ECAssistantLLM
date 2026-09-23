using System.Collections.Concurrent;
using ECAssistant.LLM.Interfaces;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Manages client connections: registration, heartbeat, eviction.
/// The server STAYS ALIVE regardless of client state — model stays in memory.
/// Shutdown happens ONLY via the explicit /eca/shutdown endpoint or a process signal
/// (Ctrl+C / SIGTERM). No last-client, idle, or grace-based shutdown logic.
/// </summary>
public sealed class ClientManager : IClientManager, IDisposable
{
    private readonly ConcurrentDictionary<string, ClientRecord> _clients = new();
    private readonly SessionRegistry _sessionRegistry;
    private readonly Engine.Backends.ProcessSessionRegistry? _processSessionRegistry;
    private readonly ILogger _logger;

    public ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _processSessionRegistry = null;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Eviction DISABLED (Emre, 2026-09-23): clients live until explicit Disconnect.
        // Idle/briefly-disconnected clients must never 401 mid-session.
    }

    public ClientManager(
        SessionRegistry sessionRegistry,
        ECAssistant.LLM.Config.LlmServerConfig config,
        ILogger logger,
        Action? onLastClientDisconnected = null,
        Engine.Backends.ProcessSessionRegistry? processSessionRegistry = null)
    {
        // onLastClientDisconnected is intentionally ignored — the server never
        // self-shuts down anymore. Kept as an optional parameter for call-site
        // compatibility (Program.cs still passes its cts-cancel callback).
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _processSessionRegistry = processSessionRegistry;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Eviction DISABLED (Emre, 2026-09-23): clients live until explicit Disconnect.
        // Idle/briefly-disconnected clients must never 401 mid-session.
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

    /// <summary>
    /// Disconnect a client (frees all its sessions).
    /// The server stays alive — model remains loaded in memory for the next client.
    /// </summary>
    public bool Disconnect(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var record))
            return false;

        var freed = _sessionRegistry.DestroyClientSessions(clientId);
        freed += _processSessionRegistry?.DestroyClient(clientId) ?? 0;
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

    public void Dispose()
    {
        // Eviction removed (2026-09-23): nothing periodic to dispose.
    }
}

/// <summary>Internal client record.</summary>
internal sealed class ClientRecord
{
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public DateTime RegisteredAt { get; }
    public int ActiveSessions { get; set; }

    public ClientRecord(string id, string name, string version, DateTime registeredAt)
    {
        Id = id;
        Name = name;
        Version = version;
        RegisteredAt = registeredAt;
        ActiveSessions = 0;
    }
}
