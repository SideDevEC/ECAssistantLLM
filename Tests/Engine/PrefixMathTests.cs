using LLama.Native;
using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Tests.Engine;

/// <summary>
/// Pure-math tests for prompt-cache common-prefix bookkeeping.
/// </summary>
public class PrefixMathTests
{
    private static LLamaToken[] Tokens(params int[] values)
        => values.Select(v => (LLamaToken)v).ToArray();

    [Fact]
    public void CommonPrefixLength_IdenticalArrays_ReturnsFullLength()
    {
        var a = Tokens(1, 2, 3, 4);
        Assert.Equal(4, PrefixMath.CommonPrefixLength(a, a));
    }

    [Fact]
    public void CommonPrefixLength_TotalDivergence_ReturnsZero()
    {
        Assert.Equal(0, PrefixMath.CommonPrefixLength(Tokens(1, 2, 3), Tokens(9, 8, 7)));
    }

    [Fact]
    public void CommonPrefixLength_PartialMatch_ReturnsDivergencePoint()
    {
        // "instruction header" tokens shared, then divergence
        var cached = Tokens(10, 11, 12, 50, 51);
        var fresh = Tokens(10, 11, 12, 99, 98, 97);
        Assert.Equal(3, PrefixMath.CommonPrefixLength(cached, fresh));
    }

    [Fact]
    public void CommonPrefixLength_EmptyCache_ReturnsZero()
    {
        Assert.Equal(0, PrefixMath.CommonPrefixLength(Array.Empty<LLamaToken>(), Tokens(1, 2)));
    }

    [Fact]
    public void CommonPrefixLength_NullInput_ReturnsZero()
    {
        Assert.Equal(0, PrefixMath.CommonPrefixLength(null!, Tokens(1, 2)));
    }

    [Fact]
    public void CommonPrefixLength_GrownPrompt_MatchesCachedPortion()
    {
        // append-growth case: cache holds whole old prompt, new prompt extends it
        var cached = Tokens(1, 2, 3);
        var grown = Tokens(1, 2, 3, 4, 5);
        Assert.Equal(3, PrefixMath.CommonPrefixLength(cached, grown));
    }
}
