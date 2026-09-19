using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

/// <summary>
/// RootPathGuard: paths inside the root pass through; absolute paths outside the
/// root are relocated under it. The server must never write outside its root.
/// </summary>
public sealed class RootPathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rootguard").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnsureInside_RelativePath_StaysUnderRoot()
    {
        var (path, relocated) = RootPathGuard.EnsureInside(_root, "backends/model.pid");

        Assert.False(relocated);
        Assert.StartsWith(_root, path);
        Assert.EndsWith("model.pid", path);
    }

    [Fact]
    public void EnsureInside_AbsoluteInsideRoot_PassesThrough()
    {
        var inside = Path.Combine(_root, "logs", "server.log");

        var (path, relocated) = RootPathGuard.EnsureInside(_root, inside);

        Assert.False(relocated);
        Assert.Equal(Path.GetFullPath(inside), path);
    }

    [Fact]
    public void EnsureInside_AbsoluteOutsideRoot_RelocatesUnderRoot()
    {
        var (path, relocated) = RootPathGuard.EnsureInside(_root, "/var/log/ecassistant-llm.log");

        Assert.True(relocated);
        Assert.Equal(Path.Combine(_root, "ecassistant-llm.log"), path);
    }

    [Fact]
    public void EnsureInside_DirectoryOutsideRoot_RelocatesUnderRoot()
    {
        var (path, relocated) = RootPathGuard.EnsureInside(_root, "/opt/something/backends");

        Assert.True(relocated);
        Assert.Equal(Path.Combine(_root, "backends"), path);
    }

    [Fact]
    public void EnsureInside_RootItself_PassesThrough()
    {
        var (path, relocated) = RootPathGuard.EnsureInside(_root, _root);

        Assert.False(relocated);
        Assert.Equal(Path.GetFullPath(_root), path);
    }
}
