using System.Text.Json;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Models;
using Xunit;

namespace ECAssistant.LLM.Tests;

public class GrammarProbeTests
{
    [Fact]
    public void Probe()
    {
        var tool = new OpenAiToolSpec { Function = new OpenAiFunctionSpec
        {
            Name = "ECodeEditor",
            Parameters = JsonDocument.Parse("""
                {"type":"object","required":["action","file"],"properties":{"action":{"type":"string","enum":["create","patch","delete"]},"file":{"type":"string"},"content":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}}}
                """).RootElement,
        }};
        var g = ToolCallGrammarFactory.Build(new[] { tool });
        File.WriteAllText("/tmp/probe-grammar.txt", g);
    }
}
