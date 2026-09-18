using System.Diagnostics;
using System.Net.Http;
using ECAssistant.LLM.Config;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// One externally-served model: owns a llama-server child process and its port.
/// Health-checked startup, graceful shutdown, disposable.
/// </summary>
public sealed class ProcessModelInstance : IDisposable
{
    private readonly ModelConfig _config;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly string _binaryPath;
    private readonly int _port;
    private Process? _process;
    private bool _disposed;

    /// <summary>Base URL the child process serves on.</summary>
    public string BaseUrl { get; }

    /// <summary>Model ID this instance serves.</summary>
    public string ModelId => _config.Id;

    /// <summary>True while the child process is alive and has passed a health check.</summary>
    public bool IsHealthy { get; private set; }

    public ProcessModelInstance(ModelConfig config, string serverBinaryPath, int port, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _binaryPath = string.IsNullOrWhiteSpace(serverBinaryPath)
            ? throw new ArgumentException("Server binary path is required", nameof(serverBinaryPath))
            : serverBinaryPath;
        _port = port > 0 ? port : throw new ArgumentException("Port must be positive", nameof(port));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        BaseUrl = $"http://127.0.0.1:{_port}";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECAssistantLLM/1.0");
    }

    /// <summary>
    /// Launches the child process and waits (up to 120s) until its /health endpoint
    /// responds 200.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
            throw new InvalidOperationException($"Model '{_config.Id}' already started");

        var args = BuildArguments(_config, _port);
        var psi = new ProcessStartInfo(_binaryPath, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        _logger.Info("ProcessBackend", $"Starting '{_config.Id}': {_binaryPath} {args}");
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start llama-server for '{_config.Id}'");

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _logger.Warn("ProcessBackend", $"llama-server for '{_config.Id}' exited (code {_process?.ExitCode})");
        _ = PumpAsync(_process.StandardError, "stderr");
        _ = PumpAsync(_process.StandardOutput, "stdout");

        await WaitForHealthyAsync(ct).ConfigureAwait(false);
        IsHealthy = true;
        _logger.Info("ProcessBackend", $"'{_config.Id}' healthy at {BaseUrl}");
    }

    /// <summary>Stops the child process (graceful, then forced after 10s).</summary>
    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited)
            return;

        try
        {
            _process.Kill(entireProcessTree: false);
            if (!_process.WaitForExitAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10)))
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
        finally
        {
            IsHealthy = false;
            _logger.Info("ProcessBackend", $"'{_config.Id}' stopped");
        }
        await Task.CompletedTask;
    }

    private async Task WaitForHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new InvalidOperationException(
                    $"llama-server for '{_config.Id}' exited before becoming healthy");

            try
            {
                using var response = await _http.GetAsync($"{BaseUrl}/health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* timeout per attempt */ }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"llama-server for '{_config.Id}' did not become healthy in time");
    }

    private async Task PumpAsync(StreamReader reader, string streamName)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                _logger.Debug($"ProcessBackend:{streamName}", $"[{_config.Id}] {line}");
        }
        catch (Exception) { /* process died */ }
    }

    private static string BuildArguments(ModelConfig config, int port)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--model ").Append(Quote(config.Path));
        sb.Append(" --host 127.0.0.1 --port ").Append(port);
        sb.Append(" --ctx-size ").Append(config.ContextSize);
        if (config.GpuLayers > 0) sb.Append(" --n-gpu-layers ").Append(config.GpuLayers);
        if (config.Threads > 0) sb.Append(" --threads ").Append(config.Threads);
        if (config.BatchSize > 0) sb.Append(" --batch-size ").Append(config.BatchSize);
        if (!string.IsNullOrWhiteSpace(config.MmprojPath)) sb.Append(" --mmproj ").Append(Quote(config.MmprojPath));
        return sb.ToString();
    }

    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _http.Dispose();
    }
}
