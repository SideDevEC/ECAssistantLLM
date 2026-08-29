using System.Text;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Stream-safe filter that removes reasoning-model thinking blocks
/// (&lt;think&gt;…&lt;/think&gt;) from generated token streams. Handles tags split
/// across token boundaries and unclosed trailing think blocks. The model still
/// benefits from internal reasoning — only the visible output is filtered.
/// </summary>
public static class ThinkFilter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    /// <summary>Wraps a token stream, suppressing everything inside think blocks.</summary>
    public static async IAsyncEnumerable<string> ApplyAsync(
        IAsyncEnumerable<string> tokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var inThink = false;
        var buf = new StringBuilder();

        await foreach (var token in tokens.WithCancellation(ct))
        {
            // Special-token artifacts (EOS emitted as text, e.g. "</s>", "<|im_end|>") — never output.
            var t = token.Trim();
            if (t.Length > 2 && t.StartsWith('<') && t.EndsWith('>') &&
                (t.Contains('|') || t == "</s>"))
                continue;

            buf.Append(token);
            while (true)
            {
                var s = buf.ToString();
                if (inThink)
                {
                    var close = s.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (close < 0)
                    {
                        var keep = LongestSuffixPrefix(s, CloseTag);
                        buf.Clear();
                        if (keep > 0) buf.Append(s[^keep..]);
                        break;
                    }
                    s = s[(close + CloseTag.Length)..];
                    inThink = false;
                    buf.Clear();
                    buf.Append(s);
                    continue;
                }

                var open = s.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                if (open < 0)
                {
                    var keep = LongestSuffixPrefix(s, OpenTag);
                    var emit = s[..^keep];
                    buf.Clear();
                    if (keep > 0) buf.Append(s[^keep..]);
                    if (emit.Length > 0) yield return emit;
                    break;
                }

                if (open > 0) yield return s[..open];
                buf.Clear();
                buf.Append(s[(open + OpenTag.Length)..]);
                inThink = true;
            }
        }

        // Flush any residual buffer (truncated/unclosed think block at stream end)
        if (!inThink && buf.Length > 0)
            yield return buf.ToString();
    }

    // Stateless utility — no mutable state.
    private static int LongestSuffixPrefix(string s, string tag)
    {
        var max = Math.Min(s.Length, tag.Length - 1);
        for (var len = max; len > 0; len--)
            if (s.EndsWith(tag[..len], StringComparison.OrdinalIgnoreCase))
                return len;
        return 0;
    }
}
