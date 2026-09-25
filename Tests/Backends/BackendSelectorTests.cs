using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

public sealed class BackendSelectorTests
{
    private readonly BackendSelector _selector = new();

    [Fact]
    public void Select_StandardGguf_DefaultsToLlamaSharp()
    {
        var cfg = new ModelConfig { Id = "main", Path = "/nonexistent/qwen3-8b-q4_k_m.gguf" };
        Assert.Equal(ModelBackendKind.Native, _selector.Select(cfg));
    }

    [Fact]
    public void Select_OverrideProcess_WinsOverDetection()
    {
        var cfg = new ModelConfig { Id = "m", Path = "/nonexistent/standard.gguf", Backend = "process" };
        Assert.Equal(ModelBackendKind.Process, _selector.Select(cfg));
    }

    [Fact]
    public void Select_OverrideLlamaSharp_WinsOverDetection()
    {
        var cfg = new ModelConfig { Id = "m", Path = "/nonexistent/standard.gguf", Backend = "native" };
        Assert.Equal(ModelBackendKind.Native, _selector.Select(cfg));
    }

    [Fact]
    public void Select_MissingFile_WithAuto_FallsBackToLlamaSharp()
    {
        // Unresolvable path → detector returns false → default backend.
        var cfg = new ModelConfig { Id = "m", Path = "/definitely/not/here.gguf", Backend = "auto" };
        Assert.Equal(ModelBackendKind.Native, _selector.Select(cfg));
    }

    [Fact]
    public void Select_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _selector.Select(null!));
    }
}
