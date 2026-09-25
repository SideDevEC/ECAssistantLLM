using ECAssistant.LLM.Engine;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Unit tests for the concurrency-hardened buffer system — no model required.
/// Validates the BatchOpBuffer FIFO contract: nothing lost, order preserved,
/// thread-safe under concurrent writers (audit fixes 2026-09-25).
/// </summary>
public class BatchSessionBufferTests
{
    [Fact]
    public void TryDequeue_ReturnsFalse_WhenNothingBuffered()
    {
        var buffer = new BatchOpBuffer();
        Assert.True(buffer.IsEmpty);
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void EnqueuePrompt_ThenDequeue_PreservesTextAndImages()
    {
        var buffer = new BatchOpBuffer();
        var images = new byte[][] { new byte[] { 1, 2 } };
        buffer.EnqueuePrompt("hello", images);

        Assert.True(buffer.TryDequeue(out var op));
        Assert.Equal(PendingMutationType.Prompt, op.Type);
        Assert.Equal("hello", op.Text);
        Assert.Same(images, op.Images);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void EnqueueMutation_ThenDequeue_PreservesType()
    {
        var buffer = new BatchOpBuffer();
        buffer.EnqueueMutation(PendingMutationType.Save);
        buffer.EnqueueMutation(PendingMutationType.Reset);

        Assert.True(buffer.TryDequeue(out var first));
        Assert.Equal(PendingMutationType.Save, first.Type);
        Assert.True(buffer.TryDequeue(out var second));
        Assert.Equal(PendingMutationType.Reset, second.Type);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void EnqueueMutation_RejectsPromptType()
    {
        var buffer = new BatchOpBuffer();
        Assert.Throws<ArgumentException>(() => buffer.EnqueueMutation(PendingMutationType.Prompt));
    }

    [Fact]
    public void FifoOrder_Preserved_AcrossMixedOperations()
    {
        // Regression for the old single-slot last-write-wins bug: Save followed by
        // Reset used to drop the Save. FIFO must keep both.
        var buffer = new BatchOpBuffer();
        buffer.EnqueueMutation(PendingMutationType.Save);
        buffer.EnqueuePrompt("turn-2");
        buffer.EnqueueMutation(PendingMutationType.Reset);

        Assert.True(buffer.TryDequeue(out var a));
        Assert.Equal(PendingMutationType.Save, a.Type);
        Assert.True(buffer.TryDequeue(out var b));
        Assert.Equal(PendingMutationType.Prompt, b.Type);
        Assert.Equal("turn-2", b.Text);
        Assert.True(buffer.TryDequeue(out var c));
        Assert.Equal(PendingMutationType.Reset, c.Type);
    }

    [Fact]
    public void ConcurrentWriters_NothingLost()
    {
        // The old TakePendingPrompt read-then-null race could silently drop a write.
        // Concurrent enqueues on a ConcurrentQueue must never lose an operation.
        var buffer = new BatchOpBuffer();
        const int writers = 8;
        const int opsPerWriter = 500;

        Parallel.For(0, writers, w =>
        {
            for (var i = 0; i < opsPerWriter; i++)
                buffer.EnqueuePrompt($"w{w}-op{i}");
        });

        var total = 0;
        while (buffer.TryDequeue(out _))
            total++;
        Assert.Equal(writers * opsPerWriter, total);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Clear_DropsAllBufferedOperations()
    {
        var buffer = new BatchOpBuffer();
        buffer.EnqueuePrompt("a");
        buffer.EnqueueMutation(PendingMutationType.Reset);
        buffer.Clear();

        Assert.True(buffer.IsEmpty);
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void PendingMutation_Factory_StoresFields()
    {
        var op = PendingMutation.ForPrompt("text", null);
        Assert.Equal(PendingMutationType.Prompt, op.Type);
        Assert.Equal("text", op.Text);

        var save = PendingMutation.For(PendingMutationType.Save);
        Assert.Equal(PendingMutationType.Save, save.Type);
        Assert.Null(save.Text);
    }

    [Fact]
    public void PendingMutationType_HasAllExpectedValues()
    {
        // Prompt was added in the 2026-09-25 concurrency hardening
        Assert.True(Enum.IsDefined(typeof(PendingMutationType), "Prompt"));
        Assert.True(Enum.IsDefined(typeof(PendingMutationType), "Save"));
        Assert.True(Enum.IsDefined(typeof(PendingMutationType), "Rewind"));
        Assert.True(Enum.IsDefined(typeof(PendingMutationType), "Reset"));
    }
}