using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// Generic API error response.
/// </summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public ErrorDetail Error { get; set; } = new();
}

public sealed class ErrorDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "server_error";

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>
/// Generic success response.
/// </summary>
public sealed class SuccessResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Client registration request.
/// </summary>
public sealed class ClientRegisterRequest
{
    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Client registration response.
/// </summary>
public sealed class ClientRegisterResponse
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("server_version")]
    public string ServerVersion { get; set; } = LlmServerInfo.Version;
}

/// <summary>
/// Session creation request.
/// </summary>
public sealed class CreateSessionRequest
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    /// <summary>v14.9: optional toolset fingerprint for pinning (see ToolsetFingerprint).</summary>
    [JsonPropertyName("tools_hash")]
    public string? ToolsHash { get; set; }
}

/// <summary>
/// Prefill request.
/// </summary>
public sealed class PrefillRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

/// <summary>
/// Prefill response.
/// </summary>
public sealed class PrefillResponse
{
    [JsonPropertyName("prefilled")]
    public bool Prefilled { get; set; }

    [JsonPropertyName("tokens")]
    public int Tokens { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Rewind response.
/// </summary>
public sealed class RewindResponse
{
    [JsonPropertyName("rewound")]
    public bool Rewound { get; set; }
}

/// <summary>
/// v15 (Emre, 2026-09-24): evaluate request — feed text into a session's KV cache
/// as prompt with bounded sampling. The KV-hygiene primitive: repaired-content
/// injection, cache warming after rewind, steering injection — without a real
/// generation turn.
/// </summary>
public sealed class EvaluateRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>Upper bound on sampled tokens after the prompt is consumed.
    /// Default 1 — LLamaSharp's executor cannot sample zero tokens; callers that
    /// need an exact tail should keep this at 1 and account for the stray sample
    /// (documented in ARCHITECTURE.md).</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
}

/// <summary>
/// Evaluate response.
/// </summary>
public sealed class EvaluateResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("prompt_chars")]
    public int PromptChars { get; set; }

    [JsonPropertyName("sampled_tokens")]
    public int SampledTokens { get; set; }

    [JsonPropertyName("approx_tokens")]
    public int ApproxTokens { get; set; }
}

/// <summary>
/// Model load request.
/// </summary>
public sealed class LoadModelRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 0;

    [JsonPropertyName("context_size")]
    public uint ContextSize { get; set; } = 4096;

    [JsonPropertyName("threads")]
    public int Threads { get; set; } = -1;

    [JsonPropertyName("is_embedding")]
    public bool IsEmbedding { get; set; } = false;
}