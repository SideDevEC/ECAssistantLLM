using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Tracks total VRAM usage across all sessions.
/// Rejects new sessions when budget exceeded.
/// </summary>
public sealed class VramBudget
{
    private readonly LlmServerConfig _config;
    private double _currentUsageMb;
    private readonly object _lock = new();

    /// <summary>Max VRAM in MB (null = unlimited).</summary>
    public int? MaxMb => _config.Server.MaxVramMb;

    /// <summary>Current estimated VRAM usage in MB.</summary>
    public double CurrentUsageMb
    {
        get { lock (_lock) { return _currentUsageMb; } }
    }

    /// <summary>Whether budget is exceeded.</summary>
    public bool IsExceeded
    {
        get
        {
            if (!_config.Server.MaxVramMb.HasValue) return false;
            // Boundary semantics match TryReserve: only strictly over budget counts as
            // exceeded (usage == budget is still acceptable, same as TryReserve's `>`).
            lock (_lock) { return _currentUsageMb > _config.Server.MaxVramMb.Value; }
        }
    }

    public VramBudget(LlmServerConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Try to reserve VRAM for a new session. Returns false if budget exceeded.
    /// </summary>
    public bool TryReserve(double estimatedMb)
    {
        if (!_config.Server.MaxVramMb.HasValue)
            return true; // unlimited

        lock (_lock)
        {
            if (_currentUsageMb + estimatedMb > _config.Server.MaxVramMb.Value)
                return false;

            _currentUsageMb += estimatedMb;
            return true;
        }
    }

    /// <summary>
    /// Release VRAM when a session is destroyed.
    /// </summary>
    public void Release(double estimatedMb)
    {
        lock (_lock)
        {
            _currentUsageMb = Math.Max(0, _currentUsageMb - estimatedMb);
        }
    }
}