using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Tests.Engine;

/// <summary>
/// v15 (Emre, 2026-09-24): pure-logic tests for truncated-envelope salvage.
/// Covers: balanced extraction under trailing garbage, truncated-answer repair
/// (sentence trim), degenerate-loop cut, toolcalls exclusion, too-short refusal,
/// and strict-decoder enforcement (repairs that cannot decode are discarded).
/// </summary>
public class EnvelopeSalvagerTests
{
    [Fact]
    public void TryRepair_CompleteEnvelope_ReturnsIt_Unchanged()
    {
        const string raw = """{"thinking": "ok", "answer": "Hello there, friend."}""";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.Equal("Hello there, friend.", repaired!.Answer);
    }

    [Fact]
    public void TryRepair_CompleteEnvelope_WithTrailingGarbage_ExtractsBalancedDocument()
    {
        // Early-stop chunk boundary: balanced envelope + partial trailing token.
        const string raw = """{"thinking": "ok", "answer": "Hello there, friend."}{"thin""";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.Equal("Hello there, friend.", repaired!.Answer);
    }

    [Fact]
    public void TryRepair_TruncatedAnswer_ClosesAndTrimsToLastSentence()
    {
        // Budget burned mid-sentence inside the answer string — the J2 failure mode.
        const string raw = """{"thinking": "Creative writing request.", "answer": "The sea whispered secrets to the shore. Waves crashed with restless foam. The tide pulled back and the tir""";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.Equal("Creative writing request.", repaired!.Thinking);
        Assert.Equal("The sea whispered secrets to the shore. Waves crashed with restless foam.", repaired.Answer);
    }

    [Fact]
    public void TryRepair_DegenerateLoop_CutsAtSecondTelling()
    {
        var telling = "The sea roared under the pale moonlight";
        var raw = "{\"thinking\": \"Story.\", \"answer\": \"" + telling + ". " + telling + ". " + telling + ". and more";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.NotEqual(repaired!.Answer, telling + ". " + telling + ". " + telling);
        // First telling is kept; the repeats are gone.
        Assert.StartsWith(telling + ".", repaired.Answer!.TrimEnd());
        Assert.DoesNotContain(telling + ". " + telling + ".", repaired.Answer);
    }

    [Fact]
    public void TryRepair_ToolcallsEnvelope_ReturnsNull_NeverHalfToolCalls()
    {
        const string raw = """{"thinking": "Need the list.", "toolcalls": [{"name": "EShellAgent", "args": {"command": "ls -l""";
        Assert.Null(EnvelopeSalvager.TryRepair(raw));
    }

    [Fact]
    public void TryRepair_TooShortAnswer_ReturnsNull()
    {
        const string raw = """{"thinking": "Hi.", "answer": "Ok. mo""";
        Assert.Null(EnvelopeSalvager.TryRepair(raw));
    }

    [Fact]
    public void TryRepair_MissingAnswerKey_ReturnsNull()
    {
        const string raw = """{"thinking": "Hello wor""";
        Assert.Null(EnvelopeSalvager.TryRepair(raw));
    }

    [Fact]
    public void TryRepair_TruncatedAnswer_WithRealNewlines_StillDecodes()
    {
        // LLamaSharp can emit raw control characters inside strings; the decoder
        // escapes them before parsing — the salvage path must survive that too.
        const string raw = "{\"thinking\": \"Poem.\", \"answer\": \"Roses are red.\nViolets are blue.\nThe ocean is deep and the sky is gra";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.Contains("Roses are red.", repaired!.Answer);
    }

    [Fact]
    public void TryRepair_DanglingEscape_IsStripped_NotFatal()
    {
        // Buffer cut inside an escape sequence (e.g. "\u00") — the dangling escape
        // after the last sentence terminator is discarded by the sentence trim, so
        // this must still salvage.
        const string raw = """{"thinking": "Cut inside escape.", "answer": "First sentence ends here. Second sentence never finishe\u0""";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.Equal("First sentence ends here.", repaired!.Answer);
    }

    [Fact]
    public void TryRepair_RepairedEnvelope_MatchesProductionShape()
    {
        // The salvaged envelope must be indistinguishable from a normal decision:
        // same property names, serializable through the same response path.
        const string raw = """{"thinking": "Greeting.", "answer": "Hello! How can I help you today? More text follows and never en""";
        var repaired = EnvelopeSalvager.TryRepair(raw);
        Assert.NotNull(repaired);
        Assert.True(repaired!.HasAnswer);
        Assert.False(repaired.HasToolCalls);
        var json = System.Text.Json.JsonSerializer.Serialize(repaired);
        Assert.Contains("\"thinking\"", json);
        Assert.Contains("\"answer\"", json);
    }
}
