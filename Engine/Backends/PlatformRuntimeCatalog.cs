namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Pinned catalog of external backend runtime builds per platform.
/// Pinned to Prism ML llama.cpp fork release <c>prism-b10685-7dffb15</c> (2026-09-17).
/// Checksums: macOS, Windows CUDA (binaries + cudart) verified at pin time; Linux pending backfill ("" = unverified).
/// </summary>
public class PlatformRuntimeCatalog
{
    private const string Release = "prism-b10685-7dffb15";
    private const string Base = "https://github.com/PrismML-Eng/llama.cpp/releases/download/" + Release;
    private const string ArchiveRoot = "llama-prism-" + Release;

    /// <summary>Manifests for the current platform. Override point for tests and remote manifests.</summary>
    public virtual IReadOnlyList<RuntimeManifest> For(PlatformId platform)
    {
        return platform switch
        {
            PlatformId.OsxArm64 => new[]
            {
                MacosArm64Metal,
                MacosArm64Cpu
            },
            PlatformId.WinX64 => new[]
            {
                WindowsCuda124,
                WindowsCpuX64
            },
            PlatformId.LinuxX64 => new[]
            {
                LinuxCuda128,
                LinuxCpuX64
            },
            _ => Array.Empty<RuntimeManifest>()
        };
    }

    private static readonly RuntimeManifest MacosArm64Metal = new(
        PlatformId.OsxArm64, "prism-llamacpp-osx-arm64",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-macos-arm64.tar.gz",
                "7fffa7a40c74f3e9bd78f3f2f9f12f9befb7b13af45d5a69c239cf3fd37b9045")
        },
        ArchiveRoot, "llama-server");

    private static readonly RuntimeManifest MacosArm64Cpu = new(
        PlatformId.OsxArm64, "prism-llamacpp-osx-arm64-cpu",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-macos-arm64.tar.gz",
                "7fffa7a40c74f3e9bd78f3f2f9f12f9befb7b13af45d5a69c239cf3fd37b9045")
        },
        ArchiveRoot, "llama-server");

    private static readonly RuntimeManifest WindowsCuda124 = new(
        PlatformId.WinX64, "prism-llamacpp-win-x64-cuda12",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-win-cuda-12.4-x64.zip",
                "7aa73f2c52081a7280fa4b02660ba902b964e461b0cfc0b652935e4a59b01c6e"),
            new RuntimeAsset($"{Base}/cudart-llama-bin-win-cuda-12.4-x64.zip",
                "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6")
        },
        "", "llama-server.exe");

    private static readonly RuntimeManifest WindowsCpuX64 = new(
        PlatformId.WinX64, "prism-llamacpp-win-x64-cpu",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-win-cpu-x64.zip", string.Empty)
        },
        "", "llama-server.exe");

    private static readonly RuntimeManifest LinuxCuda128 = new(
        PlatformId.LinuxX64, "prism-llamacpp-linux-x64-cuda12",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-linux-cuda-12.8-x64.tar.gz", string.Empty)
        },
        ArchiveRoot, "llama-server");

    private static readonly RuntimeManifest LinuxCpuX64 = new(
        PlatformId.LinuxX64, "prism-llamacpp-linux-x64-cpu",
        new[]
        {
            new RuntimeAsset($"{Base}/llama-prism-{Release}-bin-ubuntu-x64.tar.gz", string.Empty)
        },
        ArchiveRoot, "llama-server");
}
