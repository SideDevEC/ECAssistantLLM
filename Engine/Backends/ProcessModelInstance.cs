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
    private readonly string? _pidFile;

    /// <summary>Base URL the child process serves on.</summary>
    public string BaseUrl { get; }

    /// <summary>Model ID this instance serves.</summary>
    public string ModelId => _config.Id;

    /// <summary>True while the child process is alive and has passed a health check.</summary>
    public bool IsHealthy { get; private set; }

    public ProcessModelInstance(ModelConfig config, string serverBinaryPath, int port, ILogger logger, string? pidFilePath = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _binaryPath = string.IsNullOrWhiteSpace(serverBinaryPath)
            ? throw new ArgumentException("Server binary path is required", nameof(serverBinaryPath))
            : serverBinaryPath;
        _port = port > 0 ? port : throw new ArgumentException("Port must be positive", nameof(port));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pidFile = pidFilePath;
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
        WritePidFile();
        _logger.Info("ProcessBackend", $"'{_config.Id}' healthy at {BaseUrl}");
    }

    /// <summary>
    /// Cross-platform orphan record: pid + binary name. Written once the child is healthy,
    /// deleted on graceful stop. A new server instance reaps orphans via ReapOrphan.
    /// </summary>
    private void WritePidFile()
    {
        if (_pidFile is null || _process is null) return;
        try
        {
            var line = $"{_process.Id}\t{Path.GetFileNameWithoutExtension(_binaryPath)}";
            File.WriteAllText(_pidFile, line);
        }
        catch (Exception ex) { _logger.Warn("ProcessBackend", $"[{_config.Id}] pid file write failed: {ex.Message}"); }
    }

    private void DeletePidFile()
    {
        if (_pidFile is null) return;
        try { File.Delete(_pidFile); } catch { /* best effort */ }
    }

    /// <summary>
    /// Kills an orphaned child left behind by a killed parent server, cross-platform.
    /// Safety against PID reuse: the recorded binary name must match the running
    /// process's name — a recycled PID belonging to anything else is left untouched.
    /// </summary>
    public static void ReapOrphan(string pidFile, ILogger logger)
    {
        if (!File.Exists(pidFile)) return;

        string[] parts;
        try { parts = File.ReadAllText(pidFile).Trim().Split('\t'); }
        catch (Exception ex) { logger.Warn("ProcessBackend", $"pid file unreadable ({pidFile}): {ex.Message}"); return; }

        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid))
        {
            try { File.Delete(pidFile); } catch { }
            return;
        }

        var recordedName = parts[1];
        Process? proc;
        try { proc = Process.GetProcessById(pid); }
        catch { proc = null; } // already dead — stale file

        if (proc is null || proc.HasExited)
        {
            try { File.Delete(pidFile); } catch { }
            return;
        }

        if (!string.Equals(proc.ProcessName, recordedName, StringComparison.OrdinalIgnoreCase)
            && !recordedName.StartsWith(proc.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            // PID was recycled by an unrelated process — never kill it.
            // macOS note: ProcessName is truncated to 15 chars (p_comm), so the
            // prefix match is required — "llama-server-fake" vs "llama-server-fa".
            logger.Warn("ProcessBackend", $"Orphan reap skipped: PID {pid} is '{proc.ProcessName}', not '{recordedName}'");
            try { File.Delete(pidFile); } catch { }
            return;
        }

        logger.Warn("ProcessBackend", $"Reaping orphaned child '{recordedName}' (pid {pid}) from a previous server instance");
        try
        {
            proc.Kill(entireProcessTree: true);
            if (!proc.WaitForExitAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10)))
                proc.Kill();
        }
        catch (Exception ex) { logger.Warn("ProcessBackend", $"Orphan reap failed for pid {pid}: {ex.Message}"); }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }

    /// <summary>Stops the child process (graceful, then forced after 10s).</summary>
    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited)
        {
            DeletePidFile();
            return;
        }

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
            DeletePidFile();
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
        // NOTE: GpuLayerGuard clamps in-process loads only; process-backend runs rely on
        // the pinned runtime (Metal/CUDA, no Vulkan build). If a DeltaNet-MoE model is
        // ever pinned to backend:"process" on a Vulkan host, add a clamp here.
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
