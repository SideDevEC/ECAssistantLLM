using System.Collections.Concurrent;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Thread-safe FIFO buffer of pending operations for one <see cref="BatchSession"/>.
///
/// <para>Replaces the old single-slot fields (<c>_pendingPrompt</c>, <c>_pendingMutation</c>,
/// <c>_pendingImages</c>) which had a read-then-null race (a concurrent write between the
/// read and the null assignment silently dropped the write) and last-write-wins semantics
/// (Save followed by Reset dropped the Save). A concurrent queue makes every enqueue
/// atomic: nothing can be lost, and FIFO preserves request ordering.</para>
///
/// <para>Session request threads enqueue; the coordinator thread dequeues inside the
/// serialized cycle gate. <see cref="ConcurrentQueue{T}"/> provides the thread safety;
/// no additional locking is needed.</para>
/// </summary>
internal sealed class BatchOpBuffer
{
    private readonly ConcurrentQueue<PendingMutation> _ops = new();

    /// <summary>True when no operations are buffered.</summary>
    public bool IsEmpty => _ops.IsEmpty;

    /// <summary>Buffer a prompt (with optional vision media) for the coordinator to flush.</summary>
    public void EnqueuePrompt(string text, IReadOnlyList<byte[]>? images = null) =>
        _ops.Enqueue(PendingMutation.ForPrompt(text, images));

    /// <summary>Buffer a non-prompt operation (Save/Rewind/Reset).</summary>
    public void EnqueueMutation(PendingMutationType type)
    {
        if (type == PendingMutationType.Prompt)
            throw new ArgumentException("Use EnqueuePrompt for prompt operations.", nameof(type));
        _ops.Enqueue(PendingMutation.For(type));
    }

    /// <summary>Dequeue the next operation in FIFO order. Returns false when empty.</summary>
    public bool TryDequeue(out PendingMutation op) => _ops.TryDequeue(out op!);

    /// <summary>Drops all buffered operations (used when a request is cancelled before flush).</summary>
    public void Clear() => _ops.Clear();
}