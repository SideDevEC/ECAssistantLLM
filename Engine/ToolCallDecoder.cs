using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Decodes a grammar-forced tool_calls generation (OpenAI wire shape array) into
/// typed ToolCalls and validates required properties per the declared schemas
/// (required-ness can exceed grammar coverage for mixed-optional objects).
/// Stateless utility — no mutable state.
/// </summary>
public static class ToolCallDecoder
{
    public static IReadOnlyList<ToolCall> Decode(string raw, IReadOnlyList<Models.OpenAiToolSpec> tools)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidToolCallException("Empty tool-call output");

        // Grammar-valid output can still carry raw control chars inside strings —
        // sanitize exactly like StructuredDecoder does.
        var sanitized = StructuredDecoder.EscapeUnescapedControlChars(raw);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(sanitized).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidToolCallException($"Invalid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidToolCallException("Expected non-empty tool_calls array");

        var byName = tools
            .Where(t => t.IsValid)
            // v-fix: duplicate tool names made ToDictionary throw ArgumentException (uncaught
            // → HTTP 500). Treat later duplicates as unknown-name violations with a clear error.
            .GroupBy(t => t.Function!.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var calls = new List<ToolCall>();
        foreach (var tc in root.EnumerateArray())
        {
            if (tc.ValueKind != JsonValueKind.Object) throw new InvalidToolCallException("tool_calls entry is not an object");
            var fn = tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object
                ? f
                : throw new InvalidToolCallException("tool_calls entry missing function");
            var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : throw new InvalidToolCallException("tool call missing name");
            if (!byName.TryGetValue(name, out var spec))
                throw new InvalidToolCallException($"Unknown tool '{name}'");

            var argsRaw = fn.TryGetProperty("arguments", out var a) ? a : default;
            // Arguments may arrive as object (grammar shape) or string (client-supplied shape).
            JsonElement argsObj;
            if (argsRaw.ValueKind == JsonValueKind.Object) argsObj = argsRaw;
            else if (argsRaw.ValueKind == JsonValueKind.String)
            {
                try { argsObj = JsonDocument.Parse(argsRaw.GetString() ?? "{}").RootElement.Clone(); }
                catch (JsonException ex) { throw new InvalidToolCallException($"Invalid arguments JSON for '{name}': {ex.Message}"); }
            }
            else throw new InvalidToolCallException($"Arguments for '{name}' is not an object");

            ValidateRequired(argsObj, spec.Function!.Parameters, name);

            var argsJson = JsonSerializer.Serialize(argsObj, JsonOptions);
            calls.Add(new ToolCall(name, argsJson));
        }
        return calls;
    }

    /// <summary>Validates required properties declared in the schema are present.</summary>
    private static void ValidateRequired(JsonElement args, JsonElement? schema, string toolName)
    {
        if (schema is not { } s || s.ValueKind != JsonValueKind.Object) return;
        if (!s.TryGetProperty("required", out var req) || req.ValueKind != JsonValueKind.Array) return;
        foreach (var r in req.EnumerateArray())
        {
            var key = r.GetString();
            if (key is null) continue;
            if (!args.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
                throw new InvalidToolCallException($"Tool '{toolName}' missing required argument '{key}'");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
