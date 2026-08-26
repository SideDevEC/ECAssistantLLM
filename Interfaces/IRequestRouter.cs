using System.Net;

namespace ECAssistant.LLM.Interfaces;

/// <summary>
/// Interface for routing incoming HTTP requests to the appropriate handler.
/// </summary>
public interface IRequestRouter
{
    Task RouteAsync(HttpListenerContext ctx, CancellationToken ct);
}