namespace ECAssistant.LLM.Engine.Backends;

/// <summary>Execution backend for a model.</summary>
public enum ModelBackendKind
{
    /// <summary>In-process ECAssistantInference (native llama.cpp). Default.</summary>
    Native,

    /// <summary>Subprocess supervision of an external llama-server binary.</summary>
    Process
}
