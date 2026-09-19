using ECAssistant.LLM.Engine.Backends;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

/// <summary>
/// GpuLayerGuard decision table: only Vulkan + DeltaNet-MoE + configured layers clamps.
/// </summary>
public sealed class GpuLayerGuardTests
{
    [Theory]
    [InlineData(99, 0)]   // crash case: full offload → clamped to CPU
    [InlineData(20, 0)]   // crash case: low partial offload → clamped to CPU
    [InlineData(1, 0)]    // crash case: minimal offload → clamped to CPU
    public void Compute_Vulkan_DeltaNetMoe_ClampsToZero(int configured, int expected)
    {
        var decision = GpuLayerGuard.Compute(configured, "qwen3_5moe", vulkanPrimary: true);

        Assert.Equal(expected, decision.EffectiveGpuLayers);
        Assert.True(decision.Clamped);
        Assert.NotNull(decision.Reason);
        Assert.Contains("26945", decision.Reason);
    }

    [Fact]
    public void Compute_Vulkan_ZeroConfigured_Unchanged()
    {
        var decision = GpuLayerGuard.Compute(0, "qwen3_5moe", vulkanPrimary: true);

        Assert.Equal(0, decision.EffectiveGpuLayers);
        Assert.False(decision.Clamped);
    }

    [Theory]
    [InlineData("qwen3_5")]      // dense DeltaNet (4B/9B/27B) — runs on Vulkan
    [InlineData("llama")]        // classic dense
    [InlineData("qwen2vl")]      // older Qwen
    [InlineData("gemma3")]       // Gemma
    public void Compute_Vulkan_DenseArchitectures_Unchanged(string arch)
    {
        var decision = GpuLayerGuard.Compute(99, arch, vulkanPrimary: true);

        Assert.Equal(99, decision.EffectiveGpuLayers);
        Assert.False(decision.Clamped);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Compute_DeltaNetMoe_Metal_Unchanged()
    {
        var decision = GpuLayerGuard.Compute(99, "qwen3_5moe", vulkanPrimary: false);

        Assert.Equal(99, decision.EffectiveGpuLayers);
        Assert.False(decision.Clamped);
    }

    [Fact]
    public void Compute_UnknownArchitecture_Unchanged()
    {
        var decision = GpuLayerGuard.Compute(99, null, vulkanPrimary: true);

        Assert.Equal(99, decision.EffectiveGpuLayers);
        Assert.False(decision.Clamped);
    }
}
