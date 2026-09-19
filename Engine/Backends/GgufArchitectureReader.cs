using System.Buffers.Binary;
using System.Text;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Reads the <c>general.architecture</c> metadata value from a GGUF header.
/// Reads only the header + metadata key/value pairs; tensor data is never touched.
/// Stateless utility — no mutable state.
/// </summary>
public static class GgufArchitectureReader
{
    // Stateless utility — no mutable state

    private const uint GgufMagic = 0x46554747; // "GGUF" little-endian
    private const string ArchitectureKey = "general.architecture";

    /// <summary>Returns the architecture string (e.g. "qwen3_5moe") or null when absent/unreadable.</summary>
    public static string? ReadArchitecture(string ggufPath)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath))
            return null;

        try
        {
            using var stream = File.OpenRead(ggufPath);
            return ReadArchitecture(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Stream-based core for testing.</summary>
    public static string? ReadArchitecture(Stream stream)
    {
        var keys = ReadHeaderMetadata(stream);
        return keys.TryGetValue(ArchitectureKey, out var value) ? value : null;
    }

    /// <summary>
    /// Reads the GGUF header, capturing string values of scalar metadata keys.
    /// Non-string values and arrays are skipped without capture.
    /// </summary>
    private static Dictionary<string, string> ReadHeaderMetadata(Stream stream)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        Span<byte> buf8 = stackalloc byte[8];
        if (!ReadExact(stream, buf8[..4])) return result;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf8[..4]) != GgufMagic) return result;

        if (!ReadExact(stream, buf8[..4])) return result;   // version (u32)
        if (!ReadExact(stream, buf8)) return result;        // tensor count (u64, skipped)
        if (!ReadExact(stream, buf8)) return result;        // metadata kv count (u64)
        ulong kvCount = BinaryPrimitives.ReadUInt64LittleEndian(buf8[..8]);
        if (kvCount > 1_000_000) return result; // corrupt guard

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadGgufString(stream);
            if (key is null) return result;

            if (!ReadExact(stream, buf8[..4])) return result;
            var valueType = (GgufValueType)BinaryPrimitives.ReadUInt32LittleEndian(buf8[..4]);

            if (valueType == GgufValueType.String)
            {
                var value = ReadGgufString(stream);
                if (value is null) return result;
                if (!result.ContainsKey(key)) result[key] = value;
            }
            else if (!SkipValue(stream, valueType)) return result;
        }

        return result;
    }

    private static string? ReadGgufString(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        if (!ReadExact(stream, lenBuf)) return null;
        ulong len = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);
        if (len > 4096) return null; // strings are short; larger = corrupt

        var bytes = new byte[len];
        if (!ReadExact(stream, bytes)) return null;
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool SkipValue(Stream stream, GgufValueType type)
    {
        Span<byte> buf8 = stackalloc byte[8];
        switch (type)
        {
            case GgufValueType.Uint8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                return ReadExact(stream, buf8[..1]);
            case GgufValueType.Uint16:
            case GgufValueType.Int16:
                return ReadExact(stream, buf8[..2]);
            case GgufValueType.Uint32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                return ReadExact(stream, buf8[..4]);
            case GgufValueType.Uint64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                return ReadExact(stream, buf8);
            case GgufValueType.Array:
            {
                if (!ReadExact(stream, buf8[..4])) return false;
                var elemType = (GgufValueType)BinaryPrimitives.ReadUInt32LittleEndian(buf8[..4]);
                if (!ReadExact(stream, buf8)) return false;
                ulong count = BinaryPrimitives.ReadUInt64LittleEndian(buf8);
                if (count > 10_000_000) return false; // corrupt guard
                for (ulong i = 0; i < count; i++)
                {
                    if (!SkipValue(stream, elemType)) return false;
                }
                return true;
            }
            default:
                return false; // unknown type — stop parsing, return what we have
        }
    }

    private static bool ReadExact(Stream stream, Span<byte> target)
    {
        Span<byte> span = target;
        while (!span.IsEmpty)
        {
            int read = stream.Read(span);
            if (read <= 0) return false;
            span = span[read..];
        }
        return true;
    }

    private enum GgufValueType : uint
    {
        Uint8 = 0, Int8 = 1, Uint16 = 2, Int16 = 3, Uint32 = 4, Int32 = 5,
        Float32 = 6, Bool = 7, String = 8, Array = 9, Uint64 = 10, Int64 = 11, Float64 = 12
    }
}
