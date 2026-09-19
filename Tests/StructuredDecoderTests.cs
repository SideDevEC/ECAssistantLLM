using ECAssistant.LLM.Engine;
using Xunit;

namespace ECAssistant.LLM.Tests;

/// <summary>v13 structured decision envelope decoding.</summary>
public class StructuredDecoderTests
{
    [Fact]
    public void AnswerEnvelope_Decodes()
    {
        var json = """{"thinking":"Simple greeting, no task.","answer":"Hey!"}""";
        var envelope = StructuredDecoder.Decode(json);
        Assert.True(envelope.HasAnswer);
        Assert.Equal("Hey!", envelope.Answer);
        Assert.False(envelope.HasToolCalls);
    }

    [Fact]
    public void ToolCallEnvelope_Decodes()
    {
        var json = """{"thinking":"need file","toolcalls":[{"name":"read_file","args":{"path":"x.txt"}}]}""";
        var envelope = StructuredDecoder.Decode(json);
        Assert.True(envelope.HasToolCalls);
        Assert.Single(envelope.ToolCalls!);
        Assert.Equal("read_file", envelope.ToolCalls![0].Name);
        Assert.Equal("x.txt", envelope.ToolCalls[0].Args["path"]);
    }

    [Fact]
    public void NeitherAnswerNorToolcalls_Lenient_FallsBackToThinking()
    {
        // Lenient contract: thinking IS the reply when answer/toolcalls are absent
        var envelope = StructuredDecoder.Decode("""{"thinking":"hmm"}""");
        Assert.True(envelope.HasAnswer);
        Assert.Equal("hmm", envelope.Answer);
    }

    [Fact]
    public void EmptyEverything_Throws()
    {
        Assert.Throws<InvalidDecisionException>(() =>
            StructuredDecoder.Decode("""{"thinking":""}"""));
    }

    [Fact]
    public void EmptyName_Throws()
    {
        Assert.Throws<InvalidDecisionException>(() =>
            StructuredDecoder.Decode("""{"thinking":"t","toolcalls":[{"name":"","args":{}}]}"""));
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        Assert.Throws<InvalidDecisionException>(() => StructuredDecoder.Decode("not json"));
    }
}
