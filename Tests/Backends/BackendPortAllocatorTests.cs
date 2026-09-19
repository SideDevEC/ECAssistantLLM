using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;

namespace ECAssistant.LLM.Tests.Backends;

public class BackendPortAllocatorTests
{
    private static BackendsSection Section(int min, int max) => new() { PortMin = min, PortMax = max };

    [Fact]
    public void Allocate_ReturnsPortInsideRange()
    {
        var allocator = new BackendPortAllocator(Section(20000, 25000), new Random(42));
        var port = allocator.Allocate(Array.Empty<int>());
        Assert.InRange(port, 20000, 25000);
    }

    [Fact]
    public void Allocate_SkipsUsedPorts()
    {
        // Deterministic RNG probing a tiny range; all-but-one port is used.
        var allocator = new BackendPortAllocator(Section(100, 104), new Random(7));
        var used = new[] { 100, 101, 102, 103 };
        var port = allocator.Allocate(used);
        Assert.Equal(104, port);
    }

    [Fact]
    public void Allocate_NeverReturnsSamePortTwice_WhenSequentiallyCollected()
    {
        var allocator = new BackendPortAllocator(Section(20000, 20100), new Random(1));
        var used = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var port = allocator.Allocate(used);
            Assert.DoesNotContain(port, used);
            used.Add(port);
        }
    }

    [Fact]
    public void Allocate_ThrowsWhenRangeExhausted()
    {
        var allocator = new BackendPortAllocator(Section(100, 102), new Random(3));
        var used = new[] { 100, 101, 102 };
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate(used));
    }

    [Fact]
    public void Allocate_ToleratesInvertedRangeBounds()
    {
        var allocator = new BackendPortAllocator(Section(25000, 20000), new Random(5));
        var port = allocator.Allocate(Array.Empty<int>());
        Assert.InRange(port, 20000, 25000);
    }
}
