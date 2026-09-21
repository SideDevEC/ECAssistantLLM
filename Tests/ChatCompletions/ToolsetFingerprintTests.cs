using System.Text.Json;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Models;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>v14.9: toolset fingerprint — deterministic, order-insensitive (sorted by
/// function name), and sensitive to description/schema changes (pinning correctness).</summary>
public class ToolsetFingerprintTests
{
    private static OpenAiToolSpec Tool(string name, string? schema = null, string? desc = null) => new()
    {
        Function = new OpenAiFunctionSpec
        {
            Name = name,
            Description = desc,
            Parameters = schema != null ? JsonDocument.Parse(schema).RootElement : null,
        }
    };

    [Fact]
    public void Compute_SameToolset_SameHash()
    {
        var a = ToolsetFingerprint.Compute(new[] { Tool("f", """{"type":"object"}""") });
        var b = ToolsetFingerprint.Compute(new[] { Tool("f", """{"type":"object"}""") });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DifferentOrder_SameHash()
    {
        var a = ToolsetFingerprint.Compute(new[] { Tool("f"), Tool("g") });
        var b = ToolsetFingerprint.Compute(new[] { Tool("g"), Tool("f") });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_ChangedSchema_DifferentHash()
    {
        var a = ToolsetFingerprint.Compute(new[] { Tool("f", """{"type":"object"}""") });
        var b = ToolsetFingerprint.Compute(new[] { Tool("f", """{"type":"object","required":["x"]}""") });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ChangedDescription_DifferentHash()
    {
        var a = ToolsetFingerprint.Compute(new[] { Tool("f", desc: "one") });
        var b = ToolsetFingerprint.Compute(new[] { Tool("f", desc: "two") });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_IsSha256Hex()
    {
        var h = ToolsetFingerprint.Compute(new[] { Tool("f") });
        Assert.Equal(64, h.Length);
        Assert.All(h, c => Assert.True(Uri.IsHexDigit(c)));
    }
}
