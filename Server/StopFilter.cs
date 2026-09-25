using System.Text;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Stream-safe stop-sequence filter. Terminates generation as soon as any stop
/// sequence appears in the accumulated output and truncates the emitted text
/// before it (stop sequences are never part of the response — OpenAI semantics).
/// Handles sequences split across token boundaries: a partial sequence at the
/// buffer tail is held back until it can be matched or ruled out.
/// Migration parity: replaces LLamaSharp's executor-level stop handling —
/// ECAssistantInference has no native stop support, so this runs at the stream
/// level. Disposing the wrapped source (break/early-stop) aborts generation.
/// NOT applied to grammar-constrained arms (structured/tools): a stop string
/// inside a legitimate answer value would truncate the envelope mid-JSON —
/// there the grammar + early-stop bound generation.
/// </summary>
public static class StopFilter
{
    /// <summary>Wraps a token stream, truncating at the first stop sequence.</summary>
    public static async IAsyncEnumerable<string> ApplyAsync(
        IAsyncEnumerable<string> tokens,
        IReadOnlyList<string>? stop,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var seqs = stop?
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray() ?? Array.Empty<string>();

        if (seqs.Length == 0)
        {
            await foreach (var t in tokens.WithCancellation(ct))
                yield return t;
            yield break;
        }

        var buf = new StringBuilder();
        var hit = false;

        await foreach (var token in tokens.WithCancellation(ct))
        {
            buf.Append(token);
            var s = buf.ToString();

            // First occurrence of any stop sequence → emit the prefix, abort generation.
            var idx = -1;
            var len = 0;
            for (var i = 0; i < seqs.Length; i++)
            {
                var found = s.IndexOf(seqs[i], StringComparison.Ordinal);
                if (found >= 0 && (idx < 0 || found < idx))
                {
                    idx = found;
                    len = seqs[i].Length;
                }
            }

            if (idx >= 0)
            {
                var emit = s[..idx];
                if (emit.Length > 0) yield return emit;
                hit = true;
                break; // disposes the source → generation aborted server-side
            }

            // Hold back the longest suffix that could be a stop-sequence prefix.
            var keep = 0;
            foreach (var seq in seqs)
                keep = Math.Max(keep, LongestSuffixPrefix(s, seq));

            var outText = s[..^keep];
            buf.Clear();
            if (keep > 0) buf.Append(s[^keep..]);
            if (outText.Length > 0) yield return outText;
        }

        // Generation ended without a trigger — flush the held-back partial text.
        if (!hit && buf.Length > 0)
            yield return buf.ToString();
    }

    // Stateless utility — no mutable state.
    private static int LongestSuffixPrefix(string s, string prefix)
    {
        var max = Math.Min(s.Length, prefix.Length - 1);
        for (var n = max; n > 0; n--)
        {
            if (s.EndsWith(prefix[..n], StringComparison.Ordinal))
                return n;
        }
        return 0;
    }
}