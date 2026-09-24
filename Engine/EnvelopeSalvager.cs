using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// v15: Repair pass for failed decision-envelope decodes. Two content-preserving
/// repairs, attempted in order:
///
/// 1. Balanced extraction — the envelope completed but trailing tokens/partial
///    tokens rode along in the stream buffer (early-stop breaks per chunk, so the
///    closing brace can carry garbage suffix). Re-extract the balanced JSON
///    document and decode it.
///
/// 2. Truncated-answer repair — generation burned its full token budget inside the
///    answer string (degenerate repetition), so the envelope never closes. Extract
///    the answer value, cut degenerate loops at the second occurrence, trim to the
///    last sentence terminator, then close the string and object.
///
/// Gates (all must hold — otherwise callers fall through to the 422 path):
/// - the raw text must contain an "answer" key with a non-empty value; toolcalls
///   envelopes have none and can never be salvaged (half tool calls must never
///   be delivered)
/// - the repaired answer must survive sentence trimming with a minimum length
/// - the repaired envelope must pass the strict StructuredDecoder — a repair that
///   cannot decode is discarded, never force-delivered
/// </summary>
public static class EnvelopeSalvager
{
    // Stateless utility — no mutable state (pure functions on inputs).

    /// <summary>Below this many trimmed answer characters a salvage is refused —
    /// too little content to be worth overriding the strict-decode failure.</summary>
    private const int MinAnswerChars = 20;

    /// <summary>Word n-gram size for degenerate-loop detection.</summary>
    private const int LoopGramWords = 6;

    /// <summary>Loop cut trigger: a 6-gram appearing this many times means the
    /// model re-told its own content — everything from the second telling on is
    /// discarded (first telling is kept).</summary>
    private const int LoopCutOccurrences = 2;

    public static DecisionEnvelope? TryRepair(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 10)
            return null;

        // Repair 1: a complete balanced envelope may be buried under trailing
        // garbage (early-stop chunk boundaries). Prefer this — it is exact.
        var balanced = ExtractBalancedEnvelope(raw);
        if (balanced != null)
        {
            try
            {
                var envelope = StructuredDecoder.Decode(balanced);
                if (envelope.HasAnswer || envelope.HasToolCalls) return envelope;
            }
            catch { /* fall through to truncated repair */ }
        }

        // Repair 2: truncated answer — extract, loop-cut, sentence-trim, close.
        return RepairTruncatedAnswer(raw);
    }

    /// <summary>Scan for the first balanced {...} document that decodes. Returns
    /// the raw JSON substring, or null. Mirrors the router's early-stop heuristic:
    /// the grammar produces no nested braces outside strings, so brace balance in
    /// the presence of string-state tracking identifies the document end.</summary>
    private static string? ExtractBalancedEnvelope(string raw)
    {
        var firstBrace = raw.IndexOf('{');
        if (firstBrace < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = firstBrace; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return raw.Substring(firstBrace, i - firstBrace + 1);
            }
        }
        return null;
    }

    /// <summary>Truncated-answer repair: locate the answer value inside the
    /// unterminated envelope, clean it, close the document, re-decode strictly.</summary>
    private static DecisionEnvelope? RepairTruncatedAnswer(string raw)
    {
        var valueStart = FindAnswerValueStart(raw);
        if (valueStart < 0) return null;

        // The value runs to the end of the buffer (the envelope never closed).
        // Work on the raw slice — JSON escaping is preserved as generated.
        var value = raw[valueStart..];

        value = TrimDegenerateLoop(value);
        value = TrimToLastSentence(value);
        value = StripDanglingEscape(value);

        if (value.Trim().Length < MinAnswerChars) return null;

        // Close the answer string and the envelope object. Everything before the
        // value start is the model's own well-escaped prefix (thinking + key).
        var candidate = raw[..valueStart] + value + "\"}";

        try
        {
            var envelope = StructuredDecoder.Decode(candidate);
            return envelope.HasAnswer ? envelope : null;
        }
        catch
        {
            // A repair that cannot pass the strict decoder is discarded — never
            // force-delivered.
            return null;
        }
    }

    /// <summary>Locate the first character of the answer VALUE (just past its
    /// opening quote). Returns -1 when the key is absent (toolcalls envelopes).</summary>
    private static int FindAnswerValueStart(string raw)
    {
        var keyIdx = raw.IndexOf("\"answer\"", StringComparison.Ordinal);
        if (keyIdx < 0) return -1;

        var colonIdx = raw.IndexOf(':', keyIdx + "\"answer\"".Length);
        if (colonIdx < 0) return -1;

        var quoteIdx = raw.IndexOf('"', colonIdx + 1);
        if (quoteIdx < 0) return -1;

        return quoteIdx + 1;
    }

    /// <summary>Cut degenerate repetition: probe with the value's own opening
    /// 6-word gram; when it re-occurs, keep only the first telling.</summary>
    private static string TrimDegenerateLoop(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < LoopGramWords * LoopCutOccurrences) return value;

        var probe = string.Join(' ', words[..LoopGramWords]);
        if (probe.Length < 10) return value;

        // First occurrence is at the value start by construction; find the next.
        var second = value.IndexOf(probe, probe.Length + 1, StringComparison.Ordinal);
        if (second < 0) return value;

        return value[..second];
    }

    /// <summary>Trim to the last sentence terminator so the delivered answer ends
    /// cleanly. JSON escaping never encodes . ! ? as escape pairs, so scanning the
    /// raw slice is safe; anything after the cut (dangling escapes, partial words)
    /// is discarded.</summary>
    private static string TrimToLastSentence(string value)
    {
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] is '.' or '!' or '?')
                return value[..(i + 1)];
        }
        return value;
    }

    /// <summary>Remove a trailing incomplete escape sequence (buffer cut inside
    /// e.g. "\u00" or "\\" pairs) — an odd trailing backslash run is dangling.</summary>
    private static string StripDanglingEscape(string value)
    {
        var backslashes = 0;
        for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1 ? value[..^1] : value;
    }
}
