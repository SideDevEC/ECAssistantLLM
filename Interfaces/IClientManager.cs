using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Interfaces;

/// <summary>
/// Interface for managing client connections: registration, heartbeat, eviction.
/// </summary>
public interface IClientManager
{
    int ClientCount { get; }
    string Register(string clientName, string? version = null);
    bool Heartbeat(string clientId, int activeSessions);
    bool Disconnect(string clientId);

    bool IsValid(string clientId);
    void Dispose();
}