using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Chooses the execution backend for a model configuration. Ternary-packed models are
/// routed to the Process backend; everything else stays on in-process LLamaSharp.
/// </summary>
public sealed class BackendSelector
{
    /// <summary>Default value of the optional per-model "backend" override.</summary>
    public const string AutoOverride = "auto";

    /// <summary>
    /// Select the backend for <paramref name="config"/>. The optional config override
    /// ("llamasharp" / "process") wins; otherwise ternary detection decides.
    /// </summary>
    public ModelBackendKind Select(ModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        switch (config.Backend?.Trim().ToLowerInvariant())
        {
            case "llamasharp":
                return ModelBackendKind.LlamaSharp;
            case "process":
                return ModelBackendKind.Process;
        }

        return TernaryModelDetector.IsTernary(config.Path)
            ? ModelBackendKind.Process
            : ModelBackendKind.LlamaSharp;
    }
}
