using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// v13 Parses the grammar-forced decision envelope into a typed DTO.
/// Grammar guarantees valid JSON; this adds shape validation and lenient
/// error reporting for defense in depth.
/// Stateless utility — no mutable state.
/// </summary>
public static class StructuredDecoder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Decode raw model output into a DecisionEnvelope. Throws on invalid shape.</summary>
    public static DecisionEnvelope Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDecisionException("Empty model output");

        DecisionEnvelope envelope;
        // Defense in depth: the grammar's string rule can still let raw control
        // characters (\n, \t …) through — llama.cpp GBNF character-class handling
        // is not reliable for these. Escaped them ("\n", "\u0001" …) before parsing
        // so System.Text.Json never rejects the document for them.
        var sanitized = EscapeUnescapedControlChars(raw);
        try
        {
            envelope = JsonSerializer.Deserialize<DecisionEnvelope>(sanitized, Options)
                       ?? throw new InvalidDecisionException("Null envelope");
        }
        catch (JsonException ex)
        {
            throw new InvalidDecisionException($"Invalid JSON: {ex.Message}");
        }

        if (!envelope.HasAnswer && !envelope.HasToolCalls)
        {
            // Grammar-valid but empty content (models sometimes emit "answer": ""
            // on prefilled sessions). Lenient: the thinking IS the reply — mirror
            // the client-side adapter fallback instead of rejecting.
            envelope.Answer = string.IsNullOrWhiteSpace(envelope.Thinking)
                ? throw new InvalidDecisionException("Envelope has neither answer nor toolcalls")
                : envelope.Thinking;
        }

        if (envelope.HasToolCalls)
        {
            foreach (var call in envelope.ToolCalls!)
            {
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw new InvalidDecisionException("Tool call with empty name");
            }
        }

        return envelope;
    }

    /// <summary>
    /// Replace raw (unescaped) control characters inside JSON string values with
    /// proper escapes so the document becomes parseable. Characters outside
    /// strings are left untouched (already invalid JSON — parsing will reject).
    /// </summary>
    private static string EscapeUnescapedControlChars(string raw)
    {
        bool inString = false;
        bool escaped = false;

        // Fast path: no raw control characters → return as-is.
        var needsFix = false;
        foreach (var ch in raw)
        {
            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (ch == '\\') { escaped = true; }
                else if (ch == '"') { inString = false; }
                else if (ch < 0x20) { needsFix = true; break; }
            }
            else if (ch == '"')
            {
                inString = true;
            }
        }
        if (!needsFix)
            return raw;

        inString = false;
        escaped = false;
        var sb = new StringBuilder(raw.Length + 16);
        foreach (var ch in raw)
        {
            if (!inString)
            {
                if (ch == '"')
                    inString = true;
                sb.Append(ch);
                continue;
            }

            if (escaped)
            {
                escaped = false;
                sb.Append(ch);
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                sb.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inString = false;
                sb.Append(ch);
                continue;
            }

            if (ch < 0x20)
            {
                sb.Append(ch switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => FormattableString.Invariant($"\\u{(int)ch:x4}"),
                });
                continue;
            }

            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>Envelope shape violation (should be impossible under the grammar — defense in depth).</summary>
public sealed class InvalidDecisionException : Exception
{
    public InvalidDecisionException(string message) : base(message) { }
}
