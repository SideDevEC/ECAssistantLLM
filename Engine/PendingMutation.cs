namespace ECAssistant.LLM.Engine;

/// <summary>Kinds of buffered operations a <see cref="BatchSession"/> can enqueue
/// for the <see cref="BatchInferenceCoordinator"/> to apply inside a serialized cycle.</summary>
internal enum PendingMutationType
{
    /// <summary>Flush text (and optional vision media) into the conversation via <c>Prompt</c>.</summary>
    Prompt,
    Save,
    Rewind,
    Reset
}

/// <summary>
/// A buffered operation. Sessions enqueue these into their per-session
/// <see cref="BatchOpBuffer"/>; the <see cref="BatchInferenceCoordinator"/> dequeues and
/// applies them in FIFO order inside the serialized cycle gate. FIFO guarantees nothing
/// is lost and request ordering is preserved even under concurrent callers.
/// </summary>
internal sealed class PendingMutation
{
    public PendingMutationType Type { get; init; }
    /// <summary>Prompt text (kind = Prompt only).</summary>
    public string? Text { get; init; }
    /// <summary>Vision media bytes (kind = Prompt only).</summary>
    public IReadOnlyList<byte[]>? Images { get; init; }

    public static PendingMutation ForPrompt(string text, IReadOnlyList<byte[]>? images) =>
        new() { Type = PendingMutationType.Prompt, Text = text, Images = images };

    public static PendingMutation For(PendingMutationType type) => new() { Type = type };
}