using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

public sealed class TernaryModelDetectorTests
{
    private static void WriteString(BinaryWriter w, string s)
    {
        w.Write((ulong)s.Length);
        w.Write(System.Text.Encoding.UTF8.GetBytes(s));
    }

    private static void WriteKvString(BinaryWriter w, string key, string value)
    {
        WriteString(w, key);
        w.Write((uint)8); // GGUFValueType.String
        WriteString(w, value);
    }

    private static MemoryStream BuildGguf(params (string key, string value)[] metadata)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x46554747u);         // "GGUF"
            w.Write(3u);                  // version
            w.Write(0ul);                 // tensor count
            w.Write((ulong)metadata.Length);
            foreach (var (key, value) in metadata)
                WriteKvString(w, key, value);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void IsTernary_Stream_MetadataKeyContainsTernary_True()
    {
        using var gguf = BuildGguf(
            ("general.architecture", "qwen3.8"),
            ("ternary.rotation", "hadamard"));
        Assert.True(TernaryModelDetector.IsTernary(gguf));
    }

    [Fact]
    public void IsTernary_Stream_StandardMetadata_False()
    {
        using var gguf = BuildGguf(("general.architecture", "qwen3"));
        Assert.False(TernaryModelDetector.IsTernary(gguf));
    }

    [Fact]
    public void IsTernary_Stream_NotGguf_False()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.False(TernaryModelDetector.IsTernary(ms));
    }

    [Fact]
    public void IsTernary_Stream_Empty_False()
    {
        Assert.False(TernaryModelDetector.IsTernary(new MemoryStream()));
    }

    [Fact]
    public void IsTernary_File_Missing_False()
    {
        Assert.False(TernaryModelDetector.IsTernary("/definitely/not/here.gguf"));
    }

    [Fact]
    public void IsTernary_File_ContainingTernaryKey_True()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ternary-{Guid.NewGuid():N}.gguf");
        try
        {
            using (var gguf = BuildGguf(("ternary.packing", "PTQ1_0")))
            using (var fs = File.Create(path))
            {
                gguf.CopyTo(fs);
            }
            Assert.True(TernaryModelDetector.IsTernary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
