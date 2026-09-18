using System.Buffers.Binary;
using System.Text;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Detects ternary-packed GGUF models (e.g. Prism ML Bonsai-2) by reading the GGUF
/// header metadata. Stateless utility — no mutable state.
/// </summary>
public static class TernaryModelDetector
{
    // Stateless utility — no mutable state

    private const uint GgufMagic = 0x46554747; // "GGUF" little-endian

    /// <summary>
    /// Returns true when the file looks like a ternary-packed GGUF that stock llama.cpp
    /// cannot load. Reads only the header + metadata key/value pairs.
    /// </summary>
    public static bool IsTernary(string ggufPath)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath))
            return false;

        try
        {
            using var stream = File.OpenRead(ggufPath);
            return IsTernary(stream);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Stream-based core for testing.</summary>
    public static bool IsTernary(Stream stream)
    {
        var keys = ReadHeaderKeys(stream);
        return keys.Count == 0
            ? false
            : keys.Any(k => k.Contains("ternary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the GGUF header and metadata keys (skips values). Bounded by the header
    /// only; tensor data is never read.
    /// </summary>
    private static List<string> ReadHeaderKeys(Stream stream)
    {
        var keys = new List<string>();

        Span<byte> buf8 = stackalloc byte[8];
        if (!ReadExact(stream, buf8[..4])) return keys;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf8[..4]) != GgufMagic) return keys;

        if (!ReadExact(stream, buf8[..4])) return keys;   // version (u32)
        if (!ReadExact(stream, buf8)) return keys;        // tensor count (u64, skipped)
        if (!ReadExact(stream, buf8)) return keys;        // metadata kv count (u64)
        ulong kvCount = BinaryPrimitives.ReadUInt64LittleEndian(buf8[..8]);
        if (kvCount > 1_000_000) return keys; // corrupt guard

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadGgufString(stream);
            if (key is null) return keys;
            keys.Add(key);

            if (!ReadExact(stream, buf8[..4])) return keys;
            var valueType = (GgufValueType)BinaryPrimitives.ReadUInt32LittleEndian(buf8[..4]);
            if (!SkipValue(stream, valueType)) return keys;
        }

        return keys;
    }

    private static string? ReadGgufString(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        if (!ReadExact(stream, lenBuf)) return null;
        ulong len = BinaryPrimitives.ReadUInt64LittleEndian(lenBuf);
        if (len > 4096) return null; // keys are short; larger = corrupt

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
            case GgufValueType.String:
                return ReadGgufString(stream) is not null;
            case GgufValueType.Array:
            {
                if (!ReadExact(stream, buf8)) return false;
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
                return false; // unknown type — stop parsing, treat as non-ternary
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
