using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Owns and supervises llama-server subprocess instances for Process-backend models.
/// Locates the PRE-INSTALLED runtime binary (installed by the setup wizard) and the
/// model weights on disk. This server NEVER downloads anything — it is a finished
/// product; missing pieces are hard errors with clear remediation messages.
/// </summary>
public sealed class ProcessModelHost : IProcessModelHost, IDisposable
{
    private readonly Dictionary<string, ProcessModelInstance> _instances =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>In-flight starts, keyed by model id — prevents double-start under concurrent first requests.</summary>
    private readonly Dictionary<string, Task<ProcessModelInstance>> _starting =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly string _serverRoot;
    private readonly PlatformRuntimeCatalog _catalog;
    private readonly RuntimeLocator _runtimeLocator;
    private readonly BackendPortAllocator _portAllocator;
    private bool _disposed;

    /// <inheritdoc/>
    public IReadOnlyList<ProcessModelInstance> Instances
    {
        get
        {
            lock (_lock)
            {
                return _instances.Values.ToList();
            }
        }
    }

    public ProcessModelHost(
        LlmServerConfig config,
        ILogger logger,
        string serverRoot,
        PlatformRuntimeCatalog? catalog = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverRoot = string.IsNullOrWhiteSpace(serverRoot)
            ? throw new ArgumentException("Server root is required", nameof(serverRoot))
            : Path.GetFullPath(serverRoot);
        _catalog = catalog ?? new PlatformRuntimeCatalog();
        _runtimeLocator = new RuntimeLocator();
        _portAllocator = new BackendPortAllocator(_config.Backends);
    }

    /// <inheritdoc/>
    public Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        Task<ProcessModelInstance> start;
        lock (_lock)
        {
            if (_instances.TryGetValue(config.Id, out var existing))
                return Task.FromResult(existing);
            if (_starting.TryGetValue(config.Id, out var inFlight))
                return inFlight.WaitAsync(ct);

            start = StartCoreAsync(config, ct);
            _starting[config.Id] = start;
        }
        return start.WaitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default)
    {
        var cfg = _config.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown model: {modelId}");
        var instance = await EnsureStartedAsync(cfg, ct).ConfigureAwait(false);
        return instance.BaseUrl;
    }

    private async Task<ProcessModelInstance> StartCoreAsync(ModelConfig config, CancellationToken ct)
    {
        try
        {
            var backendsRoot = ResolveRootedPath(_config.Backends.BackendsRoot);
            var modelsRoot = ResolveRootedPath(_config.Backends.ModelsRoot);

            // Model weights must already be installed (wizard). No downloads, ever.
            var modelPath = ResolveModelPath(config.Path, modelsRoot);
            if (!File.Exists(modelPath))
                throw new InvalidOperationException(
                    $"Model weights not found for '{config.Id}': {modelPath}. " +
                    "Run the setup wizard to install the model.");

            // Runtime must already be installed (wizard). No downloads, ever.
            var platform = PlatformDetector.Current();
            var runtimeManifest = _catalog.For(platform).First();
            var binaryPath = _runtimeLocator.FindInstalled(backendsRoot, runtimeManifest);
            if (binaryPath is null)
                throw new InvalidOperationException(
                    $"Backend runtime '{runtimeManifest.RuntimeId}' is not installed under '{backendsRoot}'. " +
                    "Run the setup wizard — it installs everything ECAssistantLLM needs. " +
                    "The LLM server does not download runtimes at runtime.");

            // Reap any orphan left by a killed parent server BEFORE spawning fresh.
            var pidFile = Path.Combine(backendsRoot, $"{config.Id}.pid");
            ProcessModelInstance.ReapOrphan(pidFile, _logger);

            var instance = new ProcessModelInstance(config, binaryPath, AllocatePort(), _logger, pidFile);
            await instance.StartAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                _starting.Remove(config.Id);
                _instances[config.Id] = instance;
            }
            return instance;
        }
        catch
        {
            lock (_lock)
            {
                _starting.Remove(config.Id);
            }
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAllAsync()
    {
        List<ProcessModelInstance> toStop;
        lock (_lock)
        {
            toStop = _instances.Values.ToList();
            _instances.Clear();
        }
        foreach (var instance in toStop)
        {
            await instance.StopAsync().ConfigureAwait(false);
        }
    }

    private int AllocatePort()
    {
        lock (_lock)
        {
            var used = _instances.Values
                .Concat(_starting.Values.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result))
                .Select(i => int.Parse(i.BaseUrl.Split(':').Last()))
                .ToHashSet();

            // OS-verify before committing: the allocator only knows this server's own
            // instances — orphaned children or unrelated services may still hold ports.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = _portAllocator.Allocate(used);
                if (BackendPortAllocator.IsPortFree(candidate)) return candidate;
                used.Add(candidate);
                _logger.Warn("ProcessBackend", $"Port {candidate} unexpectedly busy — probing next");
            }
            throw new InvalidOperationException("No free backend port available (OS probe exhausted)");
        }
    }

    // Root-confinement: absolute config paths are relocated under the server root —
    // the server never writes outside its root on any OS.
    private string ResolveRootedPath(string relative)
    {
        if (!Path.IsPathRooted(relative))
            return Path.Combine(_serverRoot, relative);

        var (confined, relocated) = RootPathGuard.EnsureInside(_serverRoot, relative);
        if (relocated)
            _logger.Warn("ProcessModelHost", $"Path '{relative}' is outside the server root — relocated to '{confined}'");
        return confined;
    }

    private static string ResolveModelPath(string configPath, string modelsRoot)
    {
        if (Path.IsPathRooted(configPath)) return configPath;
        var candidate = Path.GetFullPath(configPath);
        return File.Exists(candidate) ? candidate : Path.Combine(modelsRoot, Path.GetFileName(configPath));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAllAsync().GetAwaiter().GetResult();
    }
}
