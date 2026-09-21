using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Config;

/// <summary>
/// Server binding and lifecycle settings.
/// </summary>
public sealed class ServerSection
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "localhost";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 8420;
    // ^ 8420 matches LlmServerConfig.GenerateDefault() — the two defaults must stay
    // aligned (GenerateDefault writes this section; a divergence here would make a
    // freshly generated config disagree with a hand-written one).

    [JsonPropertyName("max_sessions")]
    public int MaxSessions { get; set; } = 8;

    /// <summary>
    /// Max total VRAM for KV caches across all sessions (MB).
    /// null = unlimited (limited by GPU/driver).
    /// </summary>
    [JsonPropertyName("max_vram_mb")]
    public int? MaxVramMb { get; set; }

    /// <summary>
    /// DEPRECATED / IGNORED: the server never shuts down on last-client disconnect
    /// anymore — it stays alive with the model in memory until an explicit
    /// /eca/shutdown request. Kept only so old llm-server.json files parse.
    /// </summary>
    [JsonPropertyName("shutdown_on_last_client")]
    public bool ShutdownOnLastClient { get; set; } = false;

    /// <summary>DEPRECATED / IGNORED — see ShutdownOnLastClient.</summary>
    [JsonPropertyName("shutdown_grace_sec")]
    public int ShutdownGraceSec { get; set; } = 0;

    [JsonPropertyName("heartbeat_timeout_sec")]
    public int HeartbeatTimeoutSec { get; set; } = 90;

    [JsonPropertyName("heartbeat_interval_sec")]
    public int HeartbeatIntervalSec { get; set; } = 30;

    /// <summary>
    /// Restrictive root directory for runtime model loads via /eca/models/load.
    /// When set, only model paths under this directory are accepted.
    /// null = no restriction (only appropriate for fully trusted localhost setups).
    /// </summary>
    [JsonPropertyName("models_root")]
    public string? ModelsRoot { get; set; }

    /// <summary>
    /// Base URL for HttpListener prefix. e.g. http://localhost:8420/
    /// </summary>
    [JsonIgnore]
    public string Prefix => $"http://{Host}:{Port}/";
}