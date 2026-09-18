namespace ECAssistant.LLM.Engine.Backends;

/// <summary>Execution backend for a model.</summary>
public enum ModelBackendKind
{
    /// <summary>In-process LLamaSharp execution (stock llama.cpp). Default.</summary>
    LlamaSharp,

    /// <summary>Subprocess supervision of an external llama-server binary (Prism fork for ternary models).</summary>
    Process
}
