using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Tests.Fixtures;

/// <summary>
/// Second test fixture — identical to <see cref="TestServerFixture"/> but with
/// <c>continuous_batching=true</c>. Runs on a separate port (8422). The same test
/// suite runs against both fixtures to verify the batch path produces identical
/// results to the standard path.
/// </summary>
public sealed class BatchServerFixture : IAsyncLifetime
{
    public const int Port = 8422;
    public string BaseUrl => $"http://localhost:{Port}";
    public HttpClient Client { get; private set; } = null!;
    public LlmServerConfig Config { get; private set; } = null!;
    public bool IsRunning { get; private set; }

    private LlmHttpServer? _server;
    private LlmHttpServer? _stdServer;  // standard path server for comparison
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _stdCts;  // 🔴 separate CTS for the std companion server
    private Task? _serverTask;
    private Task? _stdServerTask;
    private readonly string _configDir = Path.Combine(AppContext.BaseDirectory, "test-config-batch");

    public async Task InitializeAsync()
    {
        // ── Start BOTH servers: batch (port 8422) + standard (port 8421) ──
        // The standard server is needed for comparison tests that assert
        // identical behavior between batch and non-batch paths.
        await StartBatchServerAsync();
        await StartStandardServerAsync();
        IsRunning = true;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        try
        {
            // 🔴 Audit fix (2026-09-25): the std companion server has its OWN CTS — only the
            // batch CTS was cancelled here, so the std server's RunAsync parked in
            // GetContextAsync forever and `await _stdServerTask` deadlocked the whole
            // xunit teardown (every BatchVsStandardComparison run appeared to hang AFTER
            // all 8 tests had already passed). Cancel BOTH.
            _cts?.Cancel();
            _stdCts?.Cancel();
            if (_serverTask != null) { try { await _serverTask.ConfigureAwait(false); } catch { } }
            if (_stdServerTask != null) { try { await _stdServerTask.ConfigureAwait(false); } catch { } }
        }
        catch { }
        finally
        {
            _server?.Dispose();
            _stdServer?.Dispose();
            _cts?.Dispose();
        }
        IsRunning = false;
    }

    // ── Server startup helpers ──

    private async Task StartBatchServerAsync()
    {
        var config = BuildConfig();
        Config = config;

        Directory.CreateDirectory(_configDir);
        var configPath = Path.Combine(_configDir, "llm-server-test-batch.json");
        File.WriteAllText(configPath,
            System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
            }));

        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        EnsureModelsAvailable(modelsDir);
        foreach (var m in config.Models)
        {
            var resolved = Path.Combine(modelsDir, Path.GetFileName(m.Path));
            m.Path = resolved;
        }

        var logger = new ServerLogger(LogLevel.Info, Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test-batch.log"));
        logger.DisableConsole();

        var modelHost = new MultiModelHost(config, logger, AppContext.BaseDirectory);
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);

        await modelHost.LoadAllAsync();

        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger);
        _cts = new CancellationTokenSource();

        void OnLastClientDisconnected() => _cts?.Cancel();
        var clientManager = new ClientManager(sessionRegistry, config, logger, OnLastClientDisconnected);

        var processModelHost = new ProcessModelHost(config, logger, AppContext.BaseDirectory);
        var processSessionRegistry = new ProcessSessionRegistry(processModelHost, config, logger);

        var batchExecutorHost = new BatchedExecutorHost(modelHost, config, logger);
        var batchSessionRegistry = new BatchSessionRegistry(batchExecutorHost, config, logger);

        _server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget,
            clientManager, logger, _cts, processModelHost, processSessionRegistry, batchSessionRegistry);

        _serverTask = Task.Run(() => _server.RunAsync(_cts.Token));
        await WaitForHealth(BaseUrl, _serverTask);

        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    private async Task StartStandardServerAsync()
    {
        var config = BuildConfig();
        config.Server.Port = 8421;  // standard port
        config.Server.ContinuousBatching = false;  // standard path

        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        foreach (var m in config.Models)
            m.Path = Path.Combine(modelsDir, Path.GetFileName(m.Path));

        var logger = new ServerLogger(LogLevel.Info, Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test-std-companion.log"));
        logger.DisableConsole();

        var modelHost = new MultiModelHost(config, logger, AppContext.BaseDirectory);
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);

        await modelHost.LoadAllAsync();

        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger);
        _stdCts = new CancellationTokenSource();

        void OnLastClientDisconnected() => _stdCts?.Cancel();
        var clientManager = new ClientManager(sessionRegistry, config, logger, OnLastClientDisconnected);

        var processModelHost = new ProcessModelHost(config, logger, AppContext.BaseDirectory);
        var processSessionRegistry = new ProcessSessionRegistry(processModelHost, config, logger);

        // Standard path: NO batch components (null = standard path)
        _stdServer = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget,
            clientManager, logger, _stdCts, processModelHost, processSessionRegistry, null);

        _stdServerTask = Task.Run(() => _stdServer.RunAsync(_stdCts.Token));
        await WaitForHealth("http://localhost:8421", _stdServerTask);
    }

    private static async Task WaitForHealth(string baseUrl, Task serverTask)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        using var probe = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            if (serverTask.IsCompleted)
            {
                var ex = serverTask.Exception?.GetBaseException() ?? new Exception("server exited");
                throw new InvalidOperationException($"Server on {baseUrl} failed to start", ex);
            }
            try
            {
                var resp = await probe.GetAsync("/eca/health");
                if (resp.IsSuccessStatusCode) { resp.Dispose(); return; }
                resp.Dispose();
            }
            catch { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Server on {baseUrl} did not become healthy within 90s");
    }

    // ── Helpers (mirror TestServerFixture) ──

    public async Task<string> RegisterClientAsync(string name = "test-client-batch", string? version = null)
    {
        var payload = new { client_name = name, version = version ?? "1.0.0-test" };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/eca/clients")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("client_id").GetString()!;
    }

    public async Task<(string clientId, string sessionId)> CreateSessionAsync(string? sessionId = null, string? modelId = null, string clientName = "test-client-batch")
    {
        var clientId = await RegisterClientAsync(clientName);
        sessionId ??= Guid.NewGuid().ToString("N");
        var payload = new { session_id = sessionId, model_id = modelId };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/eca/sessions")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return (clientId, sessionId);
    }

    private string? _autoClientId;
    private async Task<string> AutoClientIdAsync() => _autoClientId ??= await RegisterClientAsync("fixture-auto-batch");

    private static bool NeedsAutoClientId(string path, string method)
    {
        if (path.StartsWith("/v1/")) return true;
        if (path.StartsWith("/eca/"))
        {
            if (path == "/eca/health") return false;
            if (path == "/eca/clients" && method == "POST") return false;
            if (path == "/eca/shutdown") return false;
            if (path.StartsWith("/eca/clients/")) return false;
            return true;
        }
        return false;
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        if (NeedsAutoClientId(path, "POST"))
            req.Headers.TryAddWithoutValidation("X-Client-Id", await AutoClientIdAsync());
        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> PostJsonAsClientAsync(string path, object body, string clientId)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> PostRawAsync(string path, string rawBody, string? clientId = null, string contentType = "application/json")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, System.Text.Encoding.UTF8, contentType)
        };
        if (clientId != null && clientId != "")
            req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        else if (clientId == null && NeedsAutoClientId(path, "POST"))
            req.Headers.TryAddWithoutValidation("X-Client-Id", await AutoClientIdAsync());
        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> GetAsClientAsync(string path, string? clientId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (clientId != null)
            req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        else if (NeedsAutoClientId(path, "GET"))
            req.Headers.TryAddWithoutValidation("X-Client-Id", await AutoClientIdAsync());
        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> DeleteAsClientAsync(string path, string? clientId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, path);
        if (clientId != null)
            req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        else if (NeedsAutoClientId(path, "DELETE"))
            req.Headers.TryAddWithoutValidation("X-Client-Id", await AutoClientIdAsync());
        return await Client.SendAsync(req);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? clientId = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (clientId != null)
            req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        else if (NeedsAutoClientId(path, method.Method))
            req.Headers.TryAddWithoutValidation("X-Client-Id", await AutoClientIdAsync());
        return await Client.SendAsync(req);
    }

    // ── Internals ──

    private static LlmServerConfig BuildConfig()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "llm-server-test.json");
        var (config, err) = LlmServerConfig.TryLoad(jsonPath);
        if (config == null)
            throw new InvalidOperationException($"Test config failed to load: {err}");

        config.Server.Port = Port;
        config.Server.MaxSessions = 16;
        config.Server.MaxVramMb = null;
        config.Server.ShutdownOnLastClient = false;

        // ── ENABLE CONTINUOUS BATCHING ──
        config.Server.ContinuousBatching = true;
        // (batch_context_size removed 2026-09-25 — pool inherits model context_size)

        return config;
    }

    private static void EnsureModelsAvailable(string modelsDir)
    {
        Directory.CreateDirectory(modelsDir);
        var workspaceModels = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agent", "models");
        var canonical = Directory.Exists(workspaceModels) ? workspaceModels : modelsDir;
        var targets = new (string Out, string In)[]
        {
            ("qwen3-8b-q4_k_m.gguf", "Qwen_Qwen3-8B-Q4_K_M.gguf"),
            ("all-MiniLM-L6-v2-q4_k_m.gguf", "all-MiniLM-L6-v2-Q5_K_M.gguf")
        };

        foreach (var t in targets)
        {
            var dest = Path.Combine(modelsDir, t.Out);
            if (File.Exists(dest) || Directory.Exists(dest)) continue;
            var source = Path.Combine(canonical, t.In);
            if (!File.Exists(source))
                throw new FileNotFoundException($"Model source not found: {source}");
            try { File.CreateSymbolicLink(dest, source); }
            catch
            {
                try { File.Copy(source, dest, overwrite: false); }
                catch (Exception ex2) { throw new InvalidOperationException($"Could not make model available at {dest}: {ex2.Message}"); }
            }
        }
    }
}