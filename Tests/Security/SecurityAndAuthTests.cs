using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Tests.Security;

/// <summary>
/// Lightweight server harness for auth/security tests.
/// Does NOT load any GGUF models — only constructs the routing stack,
/// which is enough to exercise authentication, authorization, and
/// request-validation paths (all reject before touching model weights).
/// </summary>
public sealed class SecurityHarness : IDisposable
{
    public LlmHttpServer Server { get; }
    public HttpClient Client { get; }
    public LlmServerConfig Config { get; }
    private readonly CancellationTokenSource _cts = new();

    private SecurityHarness(LlmHttpServer server, HttpClient client, LlmServerConfig config)
    {
        Server = server;
        Client = client;
        Config = config;
    }

    /// <summary>Start a harness with no models_root restriction.</summary>
    public static SecurityHarness Start(string? modelsRoot)
    {
        var port = GetFreePort();
        var config = new LlmServerConfig
        {
            Server = new ServerSection
            {
                Host = "localhost",
                Port = port,
                MaxSessions = 4,
                ShutdownOnLastClient = false, // tests manage lifetime explicitly
                ModelsRoot = modelsRoot
            },
            Models = new List<ModelConfig>
            {
                new() { Id = "main", Path = "/nonexistent/main.gguf" },
                new() { Id = "embeddings", Path = "/nonexistent/embed.gguf", IsEmbedding = true }
            },
            Inference = new InferenceDefaults(),
            Logging = new LoggingSection()
        };

        var logger = new ServerLogger(LogLevel.Error);
        logger.DisableConsole();
        var modelHost = new MultiModelHost(config, logger);
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);
        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger, vramBudget);
        var clientManager = new ClientManager(sessionRegistry, config, logger);
        var cts = new CancellationTokenSource();

        var server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget, clientManager, logger, cts);
        _ = Task.Run(() => server.RunAsync(cts.Token));
        WaitForReady(port);

        var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        return new SecurityHarness(server, client, config);
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void WaitForReady(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                using var ping = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var resp = ping.GetAsync($"http://localhost:{port}/eca/health", cts.Token).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
        }
        throw new TimeoutException($"SecurityHarness server on port {port} did not become ready");
    }

    public async Task<string?> RegisterClientAsync(string name)
    {
        var body = JsonSerializer.Serialize(new { client_name = name });
        var resp = await Client.PostAsync("/eca/clients", new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("client_id").GetString();
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object? body, string? clientId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (clientId != null) req.Headers.Add("X-Client-Id", clientId);
        req.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        return await Client.SendAsync(req);
    }

    public void Dispose()
    {
        Client.Dispose();
        _cts.Cancel();
        try { Server.Dispose(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Auth: /eca/* management endpoints require a registered X-Client-Id; health is open.
/// </summary>
public class ClientAuthTests
{
    [Fact]
    public async Task EcaEndpoint_WithoutClientId_Returns_401()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var resp = await h.PostJsonAsync("/eca/sessions", new { session_id = "s1" }, clientId: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task EcaEndpoint_WithUnregisteredClientId_Returns_401()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var resp = await h.PostJsonAsync("/eca/sessions", new { session_id = "s1" }, clientId: "not-a-real-client");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutClientId_IsOpen()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var resp = await h.Client.GetAsync("/eca/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ClientRegistration_IsBootstrapException_AllowsNoHeader()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var clientId = await h.RegisterClientAsync("bootstrap-test");
        Assert.False(string.IsNullOrEmpty(clientId));

        // Registered ID must now be accepted by management endpoints
        var resp = await h.PostJsonAsync("/eca/sessions/status-x/status", null, clientId);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode); // 404 session-not-found is fine — auth passed
    }

    [Fact]
    public async Task Shutdown_Requires_Registered_Client()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var resp = await h.PostJsonAsync("/eca/shutdown", null, clientId: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

/// <summary>
/// models_root path restriction on /eca/models/load (with registered client).
/// </summary>
public class ModelPathRestrictionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eca-models-root").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadOutsideRoot_Returns_403()
    {
        using var h = SecurityHarness.Start(modelsRoot: _root);
        var clientId = await h.RegisterClientAsync("loader");

        var resp = await h.PostJsonAsync("/eca/models/load",
            new { id = "evil", path = "/etc/passwd.gguf", context_size = 512 }, clientId);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task TraversalViaDotDot_IsRejected_EvenIfLiteralPathTouchesRoot()
    {
        using var h = SecurityHarness.Start(modelsRoot: _root);
        var clientId = await h.RegisterClientAsync("loader");

        var escapee = Path.Combine(_root, "..", "escaped.gguf");
        var resp = await h.PostJsonAsync("/eca/models/load",
            new { id = "evil2", path = escapee }, clientId);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task InsideRoot_ButMissingFile_PassesRestriction_FailsAtLoad()
    {
        using var h = SecurityHarness.Start(modelsRoot: _root);
        var clientId = await h.RegisterClientAsync("loader");

        var inside = Path.Combine(_root, "missing.gguf");
        var resp = await h.PostJsonAsync("/eca/models/load",
            new { id = "m1", path = inside }, clientId);

        // The 403 wall is passed; the load itself fails because the file doesn't exist
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task NoModelsRootConfigured_DoesNotRestrict()
    {
        using var h = SecurityHarness.Start(modelsRoot: null);
        var clientId = await h.RegisterClientAsync("loader");

        var resp = await h.PostJsonAsync("/eca/models/load",
            new { id = "anywhere", path = "/some/other/place.gguf" }, clientId);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}

/// <summary>
/// Sub-route method enforcement: suffix routes must not fire for wrong methods.
/// </summary>
public class RouteMethodTests
{
    [Fact]
    public async Task ResetRoute_DoesNotShadow_MatchingSuffixPath_OnWrongMethod()
    {
        // path /eca/sessions/abc/reset via GET previously fell through to DELETE-destroy matching,
        // executing reset semantics regardless of HTTP method.
        using var h = SecurityHarness.Start(modelsRoot: null);
        var clientId = await h.RegisterClientAsync("method-probe");

        var resp = await h.Client.GetAsync("/eca/sessions/nonexistent/reset");
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }
}
