using System.Text.Json;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>
/// v12.8 regression: reasoning models (Qwen3.5) leak &lt;think&gt;…&lt;/think&gt; blocks and
/// EOS artifacts into responses. ThinkFilter must strip them even when tags are split
/// across arbitrary token boundaries — without losing the actual answer.
/// </summary>
public class ThinkFilterTests
{
    private static async Task<string> Collect(params string[] tokens)
    {
        var parts = new List<string>();
        await foreach (var t in ThinkFilter.ApplyAsync(tokens.ToAsyncEnumerable()))
            parts.Add(t);
        return string.Concat(parts);
    }

    [Fact]
    public async Task CompleteThinkBlock_Stripped()
    {
        var outp = await Collect("<think>", "reasoning here", "</think>", "The answer is 42.");
        Assert.Equal("The answer is 42.", outp);
    }

    [Fact]
    public async Task SplitTagBoundaries_DoNotLeak()
    {
        // tags split across token boundaries — the original streaming leak
        var outp = await Collect("<th", "ink>secret reasoning</th", "ink>", "Visible answer");
        Assert.Equal("Visible answer", outp);
    }

    [Fact]
    public async Task EmptyThinkBlock_Stripped()
    {
        var outp = await Collect("<think>", "\n\n", "</think>", "\n", "CHAT");
        Assert.Equal("CHAT", outp.Trim()); // content preserved, think gone
        Assert.DoesNotContain("<think>", outp);
        Assert.Contains("CHAT", outp);
    }

    [Fact]
    public async Task UnclosedThink_AtStreamEnd_Dropped()
    {
        var outp = await Collect("<think>", "endless reasoning but no closing tag");
        Assert.Equal("", outp);
    }

    [Fact]
    public async Task NormalText_PassesThrough()
    {
        var outp = await Collect("Hello!", " How can I help?");
        Assert.Equal("Hello! How can I help?", outp);
    }

    [Fact]
    public async Task EosArtifacts_Dropped()
    {
        var outp = await Collect("KV-SESSION-OK", "</s>");
        Assert.Equal("KV-SESSION-OK", outp);
        outp = await Collect("answer", "<|im_end|>");
        Assert.Equal("answer", outp);
    }

    [Fact]
    public async Task MultipleThinkBlocks_AllStripped()
    {
        var outp = await Collect("<think>a</think>", "ANSWER", "<think>b</think>", " more");
        Assert.Equal("ANSWER more", outp);
    }

    [Fact]
    public async Task CaseInsensitive_TagDetection()
    {
        var outp = await Collect("<THINK>x</THINK>", "done");
        Assert.Equal("done", outp);
    }
}
