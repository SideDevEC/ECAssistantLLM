namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Result of a GPU-layer guard decision: the layer count that should actually be used,
/// whether the configured value was clamped, and a human-readable reason when it was.
/// Immutable data — constructed by GpuLayerGuard only.
/// </summary>
public sealed record GpuLayerDecision(int EffectiveGpuLayers, bool Clamped, string? Reason);
