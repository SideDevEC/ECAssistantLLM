namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Clamps configured GPU layers when the combination would hit a known upstream crash.
/// Current rule: hybrid DeltaNet-MoE architectures (qwen3_5moe family) crash with any
/// partial GPU offload on Vulkan (llama.cpp issue #26945, unfixed upstream) — those
/// models must run CPU-only on Vulkan. Metal and CUDA are unaffected; dense models are
/// unaffected. Stateless utility — no mutable state.
/// </summary>
public static class GpuLayerGuard
{
    // Stateless utility — no mutable state

    /// <summary>GGUF architectures that crash with partial GPU offload on Vulkan.</summary>
    private static readonly string[] DeltaNetMoeArchitectures = { "qwen3_5moe" };

    /// <summary>
    /// Compute the effective GPU layer count. Returns the configured value untouched
    /// unless a known-crash combination is detected (clamped to 0, with reason).
    /// </summary>
    public static GpuLayerDecision Compute(int configuredGpuLayers, string? architecture, bool vulkanPrimary)
    {
        if (configuredGpuLayers <= 0 || !vulkanPrimary || architecture is null)
            return new GpuLayerDecision(configuredGpuLayers, Clamped: false, Reason: null);

        if (!DeltaNetMoeArchitectures.Contains(architecture, StringComparer.Ordinal))
            return new GpuLayerDecision(configuredGpuLayers, Clamped: false, Reason: null);

        return new GpuLayerDecision(
            0,
            Clamped: true,
            $"Vulkan + DeltaNet-MoE architecture '{architecture}' detected — clamping " +
            $"gpu_layers {configuredGpuLayers} → 0 (partial offload crashes, llama.cpp issue #26945; " +
            $"CPU inference is used until upstream ships a fix)");
    }
}
