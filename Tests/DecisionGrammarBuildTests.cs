using ECAssistant.LLM.Engine;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>v14.12.1: DecisionGrammar.BuildGbnf — tool-name union grammar building.</summary>
public class DecisionGrammarBuildTests
{
    [Fact]
    public void BuildGbnf_NullNames_ReturnsPermissiveBoundedGbnf()
    {
        // Bounded toolcalls array (default max 3 → {0,2}) applies even without
        // name constraints — bounding is orthogonal to the name union.
        var g = DecisionGrammar.BuildGbnf(null);
        Assert.Contains("(\",\" ws toolcall){0,2})", g);
        Assert.Contains("name ::= string", g);
    }

    [Fact]
    public void BuildGbnf_EmptyList_ReturnsPermissiveBoundedGbnf()
    {
        var g = DecisionGrammar.BuildGbnf(Array.Empty<string>());
        Assert.Contains("(\",\" ws toolcall){0,2})", g);
        Assert.Contains("name ::= string", g);
    }

    [Fact]
    public void BuildGbnf_WhitespaceOnly_ReturnsPermissiveBoundedGbnf()
    {
        // Same bounded form as null/empty — all permissive paths are bounded.
        var g = DecisionGrammar.BuildGbnf(new[] { " ", "" });
        Assert.Contains("(\",\" ws toolcall){0,2})", g);
        Assert.Contains("name ::= string", g);
    }

    [Fact]
    public void BuildGbnf_CustomMaxToolCalls_BoundsArray()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { "EShellAgent" }, maxToolCalls: 5);
        Assert.Contains("(\",\" ws toolcall){0,4})", g);
    }

    [Fact]
    public void BuildGbnf_SingleName_ContainsUnion()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { "EShellAgent" });
        // Union literals carry the JSON quotes — the grammar matches the tool
        // name exactly as JSON emits it ("EShellAgent", quotes included).
        var expected = """
name ::= "\"EShellAgent\""
""";
        Assert.Contains(expected, g);
        Assert.DoesNotContain("name ::= string", g);
        // Regression guard: the BARE form (missing JSON quotes) would force
        // {"name": EShellAgent} — invalid JSON, every toolcall would fail parsing.
        var bare = """
name ::= "EShellAgent"
""";
        Assert.DoesNotContain(bare, g);
    }

    [Fact]
    public void BuildGbnf_MultipleNames_AllPresent()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { "EShellAgent", "ECodeEditor", "EGitTool" });
        var expected = """
"\"EShellAgent\"" | "\"ECodeEditor\"" | "\"EGitTool\""
""";
        Assert.Contains(expected, g);
    }

    [Fact]
    public void BuildGbnf_TrimsAndDedups()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { " EShellAgent ", "EShellAgent", "ECodeEditor" });
        var expected = """
name ::= "\"EShellAgent\"" | "\"ECodeEditor\""
""";
        Assert.Contains(expected, g);
    }

    [Fact]
    public void BuildGbnf_EscapesQuotesAndBackslashes()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { "Bad\"Name", "Back\\slash" });
        var expected = """
"\"Bad\\\"Name\"" | "\"Back\\\\slash\""
""";
        Assert.Contains(expected, g);
    }

    [Fact]
    public void BuildGbnf_PreservesEnvelopeShape()
    {
        var g = DecisionGrammar.BuildGbnf(new[] { "EShellAgent" });
        Assert.Contains("root ::= envelope", g);
        // Grammar is a C# raw string literal — JSON quotes inside GBNF literals are backslash-escaped.
        Assert.Contains("\\\"thinking\\\"", g);
        Assert.Contains("\\\"toolcalls\\\"", g);
        Assert.Contains("toolcall ::=", g);
        Assert.Contains("string ::=", g);
    }
}
