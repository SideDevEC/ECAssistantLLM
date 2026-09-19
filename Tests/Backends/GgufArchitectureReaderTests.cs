using System.Buffers.Binary;
using System.Text;
using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

/// <summary>
/// GgufArchitectureReader against minimal synthetic GGUF streams.
/// </summary>
public sealed class GgufArchitectureReaderTests
{
    private static byte[] BuildGguf(params (string Key, uint Type, string? StrValue)[] metadata)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(0x46554747u);                 // magic "GGUF"
        w.Write(3u);                          // version
        w.Write(0UL);                         // tensor count
        w.Write((ulong)metadata.Length);      // kv count

        foreach (var (key, type, strValue) in metadata)
        {
            WriteString(w, key);
            w.Write(type);
            if (type == 8) // String
            {
                WriteString(w, strValue!);
            }
            else if (type == 4) // Uint32
            {
                w.Write(1u);
            }
            else if (type == 9) // Array of Uint32, 2 elements
            {
                w.Write(4u);
                w.Write(2UL);
                w.Write(1u);
                w.Write(2u);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    [Fact]
    public void ReadArchitecture_Qwen35Moe_ReturnsArch()
    {
        var gguf = BuildGguf(
            ("general.architecture", 8u, "qwen3_5moe"),
            ("general.name", 8u, "Qwen3.6-35B-A3B"));

        using var stream = new MemoryStream(gguf);
        Assert.Equal("qwen3_5moe", GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_DenseModel_ReturnsArch()
    {
        var gguf = BuildGguf(
            ("general.architecture", 8u, "qwen3_5"),
            ("general.parameter_count", 4u, null));

        using var stream = new MemoryStream(gguf);
        Assert.Equal("qwen3_5", GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_MissingKey_ReturnsNull()
    {
        var gguf = BuildGguf(("general.name", 8u, "no-arch-here"));

        using var stream = new MemoryStream(gguf);
        Assert.Null(GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_NonStringArchValue_ReturnsNull()
    {
        var gguf = BuildGguf(("general.architecture", 4u, null)); // Uint32, not String

        using var stream = new MemoryStream(gguf);
        Assert.Null(GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_ArrayBeforeArchKey_ParsesAcrossIt()
    {
        var gguf = BuildGguf(
            ("tokenizer.some_list", 9u, null),
            ("general.architecture", 8u, "qwen3_5moe"));

        using var stream = new MemoryStream(gguf);
        Assert.Equal("qwen3_5moe", GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_NotGguf_ReturnsNull()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var stream = new MemoryStream(bytes);
        Assert.Null(GgufArchitectureReader.ReadArchitecture(stream));
    }

    [Fact]
    public void ReadArchitecture_MissingFile_ReturnsNull()
    {
        Assert.Null(GgufArchitectureReader.ReadArchitecture("/nonexistent/path/model.gguf"));
    }
}
