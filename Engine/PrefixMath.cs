using LLama.Native;

namespace ECAssistant.LLM.Engine;

/// <summary>Pure helpers for prompt-cache bookkeeping.</summary>
public static class PrefixMath
{
    /// <summary>
    /// Length of the longest common token prefix of two arrays.
    /// Stateless utility — no mutable state, no side effects.
    /// </summary>
    public static int CommonPrefixLength(LLamaToken[] a, LLamaToken[] b)
    {
        int min = Math.Min(a?.Length ?? 0, b?.Length ?? 0);
        for (int i = 0; i < min; i++)
        {
            if (!a[i].Equals(b[i]))
                return i;
        }
        return min;
    }
}
