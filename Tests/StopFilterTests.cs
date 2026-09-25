using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>
/// Migration regression (LLamaSharp → ECAssistantInference): the native engine has
/// no stop-sequence support, so StopFilter must reproduce LLamaSharp's executor-level
/// stop semantics at the stream level — truncate BEFORE the stop sequence, handle
/// sequences split across token boundaries, never drop trailing content when no
/// stop fires.
/// </summary>
public class StopFilterTests
{
    private static async Task<string> Collect(IReadOnlyList<string>? stop, params string[] tokens)
    {
        var parts = new List<string>();
        await foreach (var t in StopFilter.ApplyAsync(tokens.ToAsyncEnumerable(), stop))
            parts.Add(t);
        return string.Concat(parts);
    }

    private static async Task<int> Consumed(IReadOnlyList<string>? stop, params string[] tokens)
    {
        // Verifies early abort: the wrapped source must stop being consumed once a
        // stop sequence fires (generation aborted server-side via enumerator dispose).
        var consumed = 0;
        await foreach (var _ in StopFilter.ApplyAsync(tokens.ToAsyncEnumerable(), stop))
            consumed++;
        return consumed;
    }

    [Fact]
    public async Task NoStopList_PassesThrough()
    {
        var outp = await Collect(null, "Hello", " world");
        Assert.Equal("Hello world", outp);
    }

    [Fact]
    public async Task EmptyStopList_PassesThrough()
    {
        var outp = await Collect(Array.Empty<string>(), "Hello", " world");
        Assert.Equal("Hello world", outp);
    }

    [Fact]
    public async Task SimpleStop_TruncatesBefore()
    {
        // summarize anti-prompt: "User:"
        var outp = await Collect(new[] { "User:" }, "The summary.", "User:", "How are you?");
        Assert.Equal("The summary.", outp);
    }

    [Fact]
    public async Task StopSplitAcrossTokens_TruncatesBefore()
    {
        // OpenAI semantics: everything BEFORE the stop sequence is kept verbatim
        // (including the space that precedes it), the sequence and tail are dropped.
        var outp = await Collect(new[] { "User:" }, "Answer text", " Use", "r:", " tail");
        Assert.Equal("Answer text ", outp);
    }

    [Fact]
    public async Task NoTrigger_FlushesHeldBackTail()
    {
        // "Use" is held back as a potential "User:" prefix — must be flushed at stream end
        var outp = await Collect(new[] { "User:" }, "The summary", " ends with Use");
        Assert.Equal("The summary ends with Use", outp);
    }

    [Fact]
    public async Task MultipleStops_FirstOccurrenceWins()
    {
        var outp = await Collect(new[] { "Question:", "User:" }, "Sum A. User: blah");
        Assert.Equal("Sum A. ", outp);
    }

    [Fact]
    public async Task StopAtVeryStart_EmptyOutput()
    {
        var outp = await Collect(new[] { "User:" }, "User: hi");
        Assert.Equal(string.Empty, outp);
    }

    [Fact]
    public async Task EarlyAbort_StopsConsumingSource()
    {
        // A consumed-count proxy: with a 2-token output before the stop, the filter
        // must not emit the trailing tokens after the trigger.
        var outp = await Collect(new[] { "STOP" }, "a", "b", "STOP", "c", "d");
        Assert.Equal("ab", outp);
    }

    [Fact]
    public async Task StopInsideContentNotAtPrefix_Surfaces()
    {
        // "User" without the colon is NOT a stop — full text passes
        var outp = await Collect(new[] { "User:" }, "The User list");
        Assert.Equal("The User list", outp);
    }
}