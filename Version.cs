namespace ECAssistant.LLM;

/// <summary>
/// Single source of truth for the server version string.
/// Referenced by health/status responses, client registration, and startup logging.
/// </summary>
public static class LlmServerInfo
{
    public const string Version = "1.0.0";
}
