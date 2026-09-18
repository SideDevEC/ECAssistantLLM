namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Locates previously-installed backend runtimes under the backends root directory.
/// Layout: <c>{backendsRoot}/{runtimeId}/{binaryRelativePath}</c>.
/// </summary>
public sealed class RuntimeLocator
{
    /// <summary>
    /// Returns the llama-server binary path when the runtime is installed and the
    /// binary exists; otherwise null.
    /// </summary>
    public string? FindInstalled(string backendsRootDir, RuntimeManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(backendsRootDir) || manifest is null)
            return null;

        var binary = Path.Combine(
            Path.GetFullPath(backendsRootDir),
            manifest.RuntimeId,
            manifest.BinaryRelativePath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(binary) ? binary : null;
    }
}
