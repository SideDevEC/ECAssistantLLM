using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

public sealed class RuntimeLocatorTests
{
    private static RuntimeManifest Manifest(string binary) => new(
        PlatformId.OsxArm64, "test-runtime",
        new[] { new RuntimeAsset("https://example.invalid/a.tar.gz", "") },
        "", binary);

    [Fact]
    public void FindInstalled_Present_ReturnsBinaryPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rt-{Guid.NewGuid():N}");
        try
        {
            var bin = Path.Combine(root, "test-runtime", "llama-server");
            Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
            File.WriteAllText(bin, "#!/bin/sh\n");

            var found = new RuntimeLocator().FindInstalled(root, Manifest("llama-server"));
            Assert.Equal(bin, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindInstalled_Missing_ReturnsNull()
    {
        var found = new RuntimeLocator().FindInstalled(
            Path.Combine(Path.GetTempPath(), $"rt-{Guid.NewGuid():N}"), Manifest("llama-server"));
        Assert.Null(found);
    }

    [Fact]
    public void FindInstalled_NullRoot_ReturnsNull()
    {
        Assert.Null(new RuntimeLocator().FindInstalled("", Manifest("llama-server")));
    }

    [Fact]
    public void FindInstalled_NormalizesForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rt-{Guid.NewGuid():N}");
        try
        {
            var bin = Path.Combine(root, "test-runtime", "bin", "llama-server");
            Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
            File.WriteAllText(bin, "x");

            var found = new RuntimeLocator().FindInstalled(root, Manifest("bin/llama-server"));
            Assert.NotNull(found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
