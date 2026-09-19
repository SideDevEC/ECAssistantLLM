using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Allocates TCP ports for child llama-server processes from a configurable,
/// randomly shuffled range. Defaults to a higher band (20000-25000) so backend
/// children never collide with the main server or other low-port services.
/// </summary>
public sealed class BackendPortAllocator
{
    private readonly BackendsSection _backends;
    private readonly Random _random;

    /// <param name="backends">Backend config section (port range).</param>
    /// <param name="random">Injectable RNG for deterministic tests.</param>
    public BackendPortAllocator(BackendsSection backends, Random? random = null)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Picks a random free port inside the configured range. Throws when the whole
    /// range is exhausted (either by this server's own instances or the usedPorts set).
    /// </summary>
    public int Allocate(IReadOnlyCollection<int> usedPorts)
    {
        ArgumentNullException.ThrowIfNull(usedPorts);

        var min = Math.Clamp(Math.Min(_backends.PortMin, _backends.PortMax), 1, 65535);
        var max = Math.Clamp(Math.Max(_backends.PortMin, _backends.PortMax), 1, 65535);
        var rangeSize = max - min + 1;
        var used = usedPorts.ToHashSet();

        // Random probing with a bounded attempt budget; exhaustive sweep as fallback so
        // a nearly-full range still yields the last free port instead of failing at 99%.
        var probes = Math.Min(rangeSize, 64);
        for (var attempt = 0; attempt < probes; attempt++)
        {
            var candidate = _random.Next(min, max + 1);
            if (!used.Contains(candidate)) return candidate;
        }

        for (var candidate = min; candidate <= max; candidate++)
        {
            if (!used.Contains(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"No free backend port available ({min}-{max}) — all {rangeSize} ports in use");
    }
}
