using System.Text.Json;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Models;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>
/// Tests for native OpenAI tools support: schema → GBNF conversion, tool-call
/// grammar shape, and ToolCallDecoder validation. No model load — pure logic.
/// </summary>
public class NativeToolsTests
{
    private static OpenAiToolSpec MakeTool(string name, string schemaJson) => new()
    {
        Type = "function",
        Function = new OpenAiFunctionSpec
        {
            Name = name,
            Description = "test",
            Parameters = JsonDocument.Parse(schemaJson).RootElement,
        }
    };

    // ── JsonSchemaGrammarConverter ──

    [Fact]
    public void Convert_StringEnum_ProducesLiteralAlternation()
    {
        var sb = new System.Text.StringBuilder();
        var schema = JsonDocument.Parse("""{"type":"string","enum":["create","patch"]}""").RootElement;
        JsonSchemaGrammarConverter.Convert(schema, "v", sb, new());
        var gbnf = sb.ToString();
        Assert.Contains("\\\"create\\\"", gbnf);
        Assert.Contains("\\\"patch\\\"", gbnf);
        Assert.Contains("v ::=", gbnf);
    }

    [Fact]
    public void Convert_ObjectAllRequired_EmitsFixedOrderRule()
    {
        var sb = new System.Text.StringBuilder();
        var schema = JsonDocument.Parse("""
            {"type":"object","required":["action","file"],"properties":{"action":{"type":"string"},"file":{"type":"string"}}}
            """).RootElement;
        JsonSchemaGrammarConverter.Convert(schema, "v", sb, new());
        var gbnf = sb.ToString();
        // Fixed order — no pair alternation
        Assert.DoesNotContain("\npair ::=", gbnf);
        Assert.Contains("v ::=", gbnf);
    }

    [Fact]
    public void Convert_MixedRequired_OmitsPairAlternation()
    {
        var sb = new System.Text.StringBuilder();
        var schema = JsonDocument.Parse("""
            {"type":"object","required":["a"],"properties":{"a":{"type":"string"},"b":{"type":"integer"}}}
            """).RootElement;
        JsonSchemaGrammarConverter.Convert(schema, "v", sb, new());
        Assert.Contains("-pair ::=", sb.ToString());
    }

    // ── ToolCallGrammarFactory ──

    [Fact]
    public void Build_TwoTools_HasNameAlternationAndArgsRules()
    {
        var grammar = ToolCallGrammarFactory.Build(new[]
        {
            MakeTool("get_weather", """{"type":"object","required":["city"],"properties":{"city":{"type":"string"}}}"""),
            MakeTool("noop", """{"type":"object"}"""),
        });
        Assert.Contains("root ::= \"[\" ws toolcall", grammar);
        Assert.Contains("\\\"get_weather\\\"", grammar);
        Assert.Contains("\\\"noop\\\"", grammar);
        Assert.Contains("toolcall ::= call-get-weather | call-noop", grammar);
    }

    [Fact]
    public void Build_NoValidTools_Throws()
    {
        Assert.Throws<ArgumentException>(() => ToolCallGrammarFactory.Build(new[] { new OpenAiToolSpec() }));
    }

    [Fact]
    public void Build_GrammarIsCompileableShape_NoDuplicateRules()
    {
        var grammar = ToolCallGrammarFactory.Build(new[]
        {
            MakeTool("t", """{"type":"object","required":["x"],"properties":{"x":{"type":"string"}}}"""),
        });
        var rules = grammar.Split('\n')
            .Where(l => l.Contains(" ::="))
            .Select(l => l.Split(" ::=")[0].Trim())
            .ToList();
        Assert.Equal(rules.Count, rules.Distinct().Count());
    }

    // ── ToolCallDecoder ──

    [Fact]
    public void Decode_GrammarShape_ProducesTypedCallsWithStringArguments()
    {
        var tools = new[] { MakeTool("f", """{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}}""") };
        var raw = """[{"type":"function","function":{"name":"f","arguments":{"a":"hello \"world\""}}}]""";
        var calls = ToolCallDecoder.Decode(raw, tools);
        var call = Assert.Single(calls);
        Assert.Equal("f", call.Name);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("hello \"world\"", doc.RootElement.GetProperty("a").GetString());
    }

    [Fact]
    public void Decode_MissingRequired_Throws()
    {
        var tools = new[] { MakeTool("f", """{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}}""") };
        var raw = """[{"type":"function","function":{"name":"f","arguments":{}}}]""";
        Assert.Throws<InvalidToolCallException>(() => ToolCallDecoder.Decode(raw, tools));
    }

    [Fact]
    public void Decode_UnknownTool_Throws()
    {
        var tools = new[] { MakeTool("f", """{"type":"object"}""") };
        var raw = """[{"type":"function","function":{"name":"g","arguments":{}}}]""";
        Assert.Throws<InvalidToolCallException>(() => ToolCallDecoder.Decode(raw, tools));
    }

    [Fact]
    public void Decode_EmptyArray_Throws()
    {
        var tools = new[] { MakeTool("f", """{"type":"object"}""") };
        Assert.Throws<InvalidToolCallException>(() => ToolCallDecoder.Decode("[]", tools));
    }
}
