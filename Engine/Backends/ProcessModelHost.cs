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
    private readonly object _lock = new();
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly string _serverRoot;
    private readonly PlatformRuntimeCatalog _catalog;
    private readonly RuntimeLocator _runtimeLocator;
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
    }

    /// <inheritdoc/>
    public async Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            if (_instances.TryGetValue(config.Id, out var existing))
                return existing;
        }

        var backendsRoot = Resolve(_config.Backends.BackendsRoot);
        var modelsRoot = Resolve(_config.Backends.ModelsRoot);

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

        var port = AllocatePort(config);
        var instance = new ProcessModelInstance(config, binaryPath, port, _logger);
        await instance.StartAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            _instances[config.Id] = instance;
        }
        return instance;
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

    private int AllocatePort(ModelConfig config)
    {
        lock (_lock)
        {
            var used = _instances.Values
                .Select(i => int.Parse(i.BaseUrl.Split(':').Last()))
                .ToHashSet();
            for (var port = 8500; port < 8600; port++)
            {
                if (!used.Contains(port)) return port;
            }
        }
        throw new InvalidOperationException("No free backend port available (8500-8599)");
    }

    private string Resolve(string relative) => Path.IsPathRooted(relative)
        ? relative
        : Path.Combine(_serverRoot, relative);

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
