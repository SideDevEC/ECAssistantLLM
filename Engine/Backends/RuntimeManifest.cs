namespace ECAssistant.LLM.Engine.Backends;

/// <summary>One downloadable archive belonging to a backend runtime.</summary>
/// <param name="Url">Archive download URL.</param>
/// <param name="Sha256">SHA-256 of the archive (hex, lowercase). Empty string = unverified.</param>
public sealed record RuntimeAsset(string Url, string Sha256);

/// <summary>
/// Immutable description of one backend runtime build (a pinned external llama-server
/// binary distribution) for a specific platform.
/// </summary>
/// <param name="Platform">Target platform of this build.</param>
/// <param name="RuntimeId">Stable identifier, used as the on-disk directory name.</param>
/// <param name="Assets">Archives to download and extract into the runtime directory (e.g. binaries + cudart).</param>
/// <param name="ArchiveRoot">Subdirectory inside the archives that contains the binaries, or "" if none.</param>
/// <param name="BinaryRelativePath">Path of the llama-server executable relative to the runtime directory.</param>
public sealed record RuntimeManifest(
    PlatformId Platform,
    string RuntimeId,
    IReadOnlyList<RuntimeAsset> Assets,
    string ArchiveRoot,
    string BinaryRelativePath)
{
    /// <summary>Whether every archive is integrity-checked.</summary>
    public bool IsChecksummed => Assets.All(a => !string.IsNullOrWhiteSpace(a.Sha256));
}
