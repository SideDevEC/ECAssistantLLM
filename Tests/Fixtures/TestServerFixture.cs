using ECAssistant.LLM;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Tests.Fixtures;

/// <summary>
/// Shared integration-test fixture. Starts the real ECAssistantLLM HTTP server
/// (HttpListener + RequestRouter + full engine pipeline) on the test port and
/// exposes a pre-configured <see cref="HttpClient"/> pointing at it.
///
/// The fixture loads the real GGUF models (qwen3-8b main + all-MiniLM embeddings),
/// so tests exercise genuine inference, KV-cache and routing behaviour.
/// </summary>
public sealed class TestServerFixture : IAsyncLifetime
{
       /// <summary>Test port for the LLM server.</summary>
    public const int Port = 8421;

       /// <summary>Base URL used by the pre-configured client.</summary>
    public string BaseUrl => $"http://localhost:{Port}";

       /// <summary>Pre-configured HTTP client pointing at the test server.</summary>
    public HttpClient Client { get; private set; } = null!;

       /// <summary>The config that the running server is using.</summary>
    public LlmServerConfig Config { get; private set; } = null!;

       /// <summary>True once the server is up and serving requests.</summary>
    public bool IsRunning { get; private set; }

    private LlmHttpServer? _server;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private readonly string _configDir =
        Path.Combine(AppContext.BaseDirectory, "test-config");

    public async Task InitializeAsync()
         {
             // ── Build a fresh config from the shipped test JSON ──────────────
          var config = BuildConfig();
          Config = config;

             // ── Copy the config (and a models/ folder) into a stable location ─
          Directory.CreateDirectory(_configDir);
          var configPath = Path.Combine(_configDir, "llm-server-test.json");
          File.WriteAllText(configPath,
              System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
                 {
                  WriteIndented = true,
                  DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
                 }));

             // Point the model paths at the real model files.
          var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
          EnsureModelsAvailable(modelsDir);
          foreach (var m in config.Models)
               {
                  var resolved = Path.Combine(modelsDir, Path.GetFileName(m.Path));
                  m.Path = resolved;
               }

             // ── Wire up the engine exactly like Program.cs does ──────────────
          var logger = new ServerLogger(LogLevel.Warn,
              Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log"));
          logger.DisableConsole();

          var modelHost = new MultiModelHost(config, logger);
          var scheduler = new InferenceScheduler(logger);
          var vramBudget = new VramBudget(config);

          modelHost.LoadAll();

          var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger);
             _cts = new CancellationTokenSource();

          void OnLastClientDisconnected() => _cts?.Cancel();
          var clientManager = new ClientManager(sessionRegistry, config, logger, OnLastClientDisconnected);

             _server = new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget, clientManager, logger, _cts);

             // ── Start the listener on a background task ──────────────────────
             _serverTask = Task.Run(() => _server.RunAsync(_cts.Token));

             // ── Wait until /eca/health actually answers (max 90 s), not a blind delay ──
          var deadline = DateTime.UtcNow.AddSeconds(90);
          HttpResponseMessage? health = null;
          using (var probeClient = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(2) })
          {
              while (DateTime.UtcNow < deadline)
              {
                  if (_serverTask.IsCompleted)
                  {
                      var ex = _serverTask.Exception?.GetBaseException()
                               ?? new Exception("server task completed without running");
                      throw new InvalidOperationException($"ECAssistantLLM test server failed to start on {BaseUrl}", ex);
                  }
                  try
                  {
                      health = await probeClient.GetAsync("/eca/health");
                      if (health.IsSuccessStatusCode) break;
                      health.Dispose(); health = null;
                  }
                  catch { /* not bound yet */ }
                  await Task.Delay(250);
              }
          }
          if (health == null || !health.IsSuccessStatusCode)
              throw new TimeoutException($"ECAssistantLLM test server did not become healthy within 90s on {BaseUrl}");
          health.Dispose();
          IsRunning = true;

             // ── Pre-configured client ────────────────────────────────────────
          Client = new HttpClient
                 {
                  BaseAddress = new Uri(BaseUrl),
                  Timeout = TimeSpan.FromMinutes(5)
                 };
         }

    public async Task DisposeAsync()
         {
          Client?.Dispose();

          try
               {
                 _cts?.Cancel();
              if (_serverTask != null)
                   {
                    try { await _serverTask.ConfigureAwait(false); }
                    catch { /* shutdown */ }
                   }
               }
          catch { /* best effort */ }
          finally
               {
                 _server?.Dispose();
                 _cts?.Dispose();
               }

          IsRunning = false;
         }

         // ── Helpers ────────────────────────────────────────────────────────────

         /// <summary>
         /// Register a client and return its generated client_id.
         /// </summary>
      public async Task<string> RegisterClientAsync(string name = "test-client", string? version = null)
         {
          var payload = new
               {
                client_name = name,
                version = version ?? "1.0.0-test"
               };
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

         /// <summary>
         /// Register a client and create a KV-cache session for it.
         /// Returns the (clientId, sessionId) pair.
         /// </summary>
      public async Task<(string clientId, string sessionId)> CreateSessionAsync(
          string? sessionId = null, string? modelId = null, string clientName = "test-client")
         {
          var clientId = await RegisterClientAsync(clientName);
          sessionId ??= Guid.NewGuid().ToString("N");

          var payload = new
               {
                session_id = sessionId,
                model_id = modelId
               };
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

         // ── Small HTTP helpers used across test categories ─────────────────────

         /// <summary>POST a JSON body to <paramref name="path"/> (OpenAI-style, no client header).</summary>
      public async Task<HttpResponseMessage> PostJsonAsync(string path, object body)
         {
          var json = System.Text.Json.JsonSerializer.Serialize(body);
          using var req = new HttpRequestMessage(HttpMethod.Post, path)
                 {
                  Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                 };
          return await Client.SendAsync(req);
         }

         /// <summary>POST a JSON body to <paramref name="path"/> with an X-Client-Id header.</summary>
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

         /// <summary>POST a raw string body (used for malformed-JSON tests).</summary>
      public async Task<HttpResponseMessage> PostRawAsync(string path, string rawBody, string? clientId = null,
          string contentType = "application/json")
         {
          using var req = new HttpRequestMessage(HttpMethod.Post, path)
                 {
                  Content = new StringContent(rawBody, System.Text.Encoding.UTF8, contentType)
                 };
          if (clientId != null)
              req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
          return await Client.SendAsync(req);
         }

         /// <summary>GET a path with an optional X-Client-Id header.</summary>
      public async Task<HttpResponseMessage> GetAsClientAsync(string path, string? clientId = null)
         {
          using var req = new HttpRequestMessage(HttpMethod.Get, path);
          if (clientId != null)
              req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
          return await Client.SendAsync(req);
         }

         /// <summary>DELETE a path with an optional X-Client-Id header.</summary>
      public async Task<HttpResponseMessage> DeleteAsClientAsync(string path, string? clientId = null)
         {
          using var req = new HttpRequestMessage(HttpMethod.Delete, path);
          if (clientId != null)
              req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
          return await Client.SendAsync(req);
         }

         /// <summary>Send an arbitrary method to a path (used for wrong-method tests).</summary>
      public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? clientId = null)
         {
          using var req = new HttpRequestMessage(method, path);
          if (clientId != null)
              req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
          return await Client.SendAsync(req);
         }

         // ── Internals ──────────────────────────────────────────────────────────

      private static LlmServerConfig BuildConfig()
         {
          var jsonPath = Path.Combine(AppContext.BaseDirectory, "llm-server-test.json");
          var (config, err) = LlmServerConfig.TryLoad(jsonPath);
          if (config == null)
              throw new InvalidOperationException($"Test config failed to load: {err}");

             // Enforce the test overrides regardless of what the shipped file says.
          config.Server.Port = Port;
          config.Server.MaxSessions = 16;
          config.Server.MaxVramMb = null;
          config.Server.ShutdownOnLastClient = false;
          config.Server.HeartbeatTimeoutSec = 300;

          return config;
         }

         /// <summary>
         /// Ensure the models/ folder next to the test assembly contains the two
         /// GGUF files the server needs. Creates symlinks to the canonical model
         /// files when they are not already present.
         /// </summary>
      private static void EnsureModelsAvailable(string modelsDir)
         {
          Directory.CreateDirectory(modelsDir);

          var canonical = "/Users/localdev/agent/models";
          var targets = new (string Out, string In)[]
                 {
                     ("qwen3-8b-q4_k_m.gguf", "Qwen_Qwen3-8B-Q4_K_M.gguf"),
                     ("all-MiniLM-L6-v2-q4_k_m.gguf", "all-MiniLM-L6-v2-Q5_K_M.gguf")
                 };

          foreach (var t in targets)
                 {
                  var dest = Path.Combine(modelsDir, t.Out);
                  if (File.Exists(dest) || Directory.Exists(dest))
                      continue;

                  var source = Path.Combine(canonical, t.In);
                  if (!File.Exists(source))
                      throw new FileNotFoundException($"Model source not found: {source}");

                  try
                       {
                        File.CreateSymbolicLink(dest, source);
                       }
                  catch (Exception ex)
                       {
                           // Fall back to a hard/regular copy if symlinks are unavailable.
                        try
                             {
                              File.Copy(source, dest, overwrite: false);
                             }
                        catch (Exception ex2)
                             {
                              throw new InvalidOperationException(
                                 $"Could not make model available at {dest}: {ex.Message} / {ex2.Message}");
                             }
                       }
                 }
         }
}
