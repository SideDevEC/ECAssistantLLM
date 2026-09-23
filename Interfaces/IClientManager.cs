using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Interfaces;

/// <summary>
/// Interface for managing client connections: registration and disconnection.
/// </summary>
public interface IClientManager
{
    int ClientCount { get; }
    string Register(string clientName, string? version = null);
    bool Disconnect(string clientId);

    bool IsValid(string clientId);
    void Dispose();
}