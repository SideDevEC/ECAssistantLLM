using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Interfaces;
using ECAssistant.LLM.Models;
using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Main HTTP server using HttpListener. Routes requests to OpenAI and ECAssistant endpoints.
/// </summary>
public sealed class LlmHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly LlmServerConfig _config;
    private readonly MultiModelHost _modelHost;
    private readonly SessionRegistry _sessionRegistry;
    private readonly IInferenceScheduler _scheduler;
    private readonly VramBudget _vramBudget;
    private readonly IClientManager _clientManager;
    private readonly ILogger _logger;
    private readonly RequestRouter _router;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public LlmHttpServer(
        LlmServerConfig config,
        MultiModelHost modelHost,
        SessionRegistry sessionRegistry,
        IInferenceScheduler scheduler,
        VramBudget vramBudget,
        IClientManager clientManager,
        ILogger logger,
        CancellationTokenSource? externalCts = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _modelHost = modelHost ?? throw new ArgumentNullException(nameof(modelHost));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _vramBudget = vramBudget ?? throw new ArgumentNullException(nameof(vramBudget));
        _clientManager = clientManager ?? throw new ArgumentNullException(nameof(clientManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use external CTS if provided (from Program.cs for shutdown coordination)
        _cts = externalCts ?? CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

        _router = new RequestRouter(
            _modelHost, _sessionRegistry, _scheduler, _vramBudget,
            _clientManager, _config, _logger, _cts);

        _listener.Prefixes.Add(_config.Server.Prefix);
    }

    /// <summary>
    /// Start listening. Blocks until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Link the per-run token with the constructor-provided _cts — do NOT overwrite it,
        // RequestRouter holds a reference to _cts for shutdown handling.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _listener.Start();

        _logger.Info("Server", $"ECAssistantLLM listening on {_config.Server.Prefix}");
        _logger.Info("Server", $"Models: {string.Join(", ", _modelHost.LoadedModelIds)}");
        _logger.Info("Server", $"Max sessions: {_config.Server.MaxSessions}");

        while (!runCts.Token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException
                                    or ObjectDisposedException
                                    or OperationCanceledException)
            {
                break; // listener stopped or disposed
            }

            // Handle each request on a background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await _router.RouteAsync(ctx, _cts.Token);
                }
                catch (System.Text.Json.JsonException)
                {
                    _logger.Warn("Server", "Invalid JSON body in request");
                    try
                    {
                        await SseStreamer.WriteJsonAsync(ctx.Response,
                            new ErrorResponse { Error = new() { Message = "Invalid JSON body", Type = "invalid_request" } },
                            400);
                    }
                    catch { /* response may already be sent */ }
                }
                catch (Exception ex)
                {
                    // Suppress noisy broken-pipe errors — client just disconnected
                    if (!ex.Message.Contains("Broken pipe") && !ex.Message.Contains("connection was closed"))
                        _logger.Error("Server", $"Unhandled error: {ex.Message}");
                    try
                    {
                        await SseStreamer.WriteJsonAsync(ctx.Response,
                            new ErrorResponse { Error = new() { Message = "Internal server error", Type = "server_error" } },
                            500);
                    }
                    catch { /* response may already be sent */ }
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }, _cts.Token);
        }

        _logger.Info("Server", "Shutting down...");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        try { _listener.Stop(); } catch { }
        _clientManager?.Dispose();
        _sessionRegistry?.Dispose();
        _modelHost?.Dispose();
    }
}