namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Root-confinement guard: the server must never write outside its root directory
/// (typically ~/.ECAssistantLLM), regardless of what an absolute path in the config
/// says. Paths already inside the root pass through; anything else is relocated under
/// the root using its final path segment. Stateless utility — no mutable state.
/// </summary>
public static class RootPathGuard
{
    // Stateless utility — no mutable state

    /// <summary>
    /// Returns <paramref name="path"/> when it resolves inside <paramref name="rootDir"/>;
    /// otherwise relocates it under the root using its final segment.
    /// </summary>
    public static (string Path, bool Relocated) EnsureInside(string rootDir, string path)
    {
        var root = Path.GetFullPath(rootDir);

        // Relative paths are relative to the root by definition — always inside.
        if (!Path.IsPathRooted(path))
            return (Path.GetFullPath(Path.Combine(root, path)), false);

        var full = Path.GetFullPath(path);

        if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || full == root)
            return (full, false);

        var finalSegment = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(finalSegment))
            finalSegment = "relocated";
        return (Path.Combine(root, finalSegment), true);
    }
}
