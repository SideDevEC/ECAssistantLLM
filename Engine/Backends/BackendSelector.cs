using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

public sealed class BackendSelector
{
    public const string AutoOverride = "auto";

    public ModelBackendKind Select(ModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        switch (config.Backend?.Trim().ToLowerInvariant())
        {
            case "native":
            case "llamasharp": // backward compat
                return ModelBackendKind.Native;
            case "process":
                return ModelBackendKind.Process;
        }

        return TernaryModelDetector.IsTernary(config.Path)
            ? ModelBackendKind.Process
            : ModelBackendKind.Native;
    }
}
