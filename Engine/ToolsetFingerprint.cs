using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Deterministic fingerprint of a toolset (OpenAI tools array) used for
/// session toolset pinning: a session pinned to fingerprint X that later
/// receives tools with fingerprint Y gets its KV cache deterministically
/// reset (tool definitions are part of the prompt — a mismatch silently
/// breaks prefix-cache reuse and pollutes the transcript context).
/// Canonical form: tools sorted by function name, serialized compactly.
/// Stateless utility — no mutable state.
/// </summary>
public static class ToolsetFingerprint
{
    public static string Compute(IReadOnlyList<OpenAiToolSpec> tools)
    {
        var canonical = tools
            .Where(t => t.IsValid)
            .OrderBy(t => t.Function!.Name, StringComparer.Ordinal)
            .Select(t => new
            {
                name = t.Function!.Name,
                description = t.Function.Description,
                parameters = t.Function.Parameters?.GetRawText(),
            });
        var json = JsonSerializer.Serialize(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
