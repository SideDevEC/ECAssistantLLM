using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Engine.Backends;
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
    private readonly SemaphoreSlim _requestGate;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Max in-flight request tasks. Bounds memory/CPU under load spikes; inference itself
    /// is additionally serialized by the scheduler. 4× cores is a sensible HTTP ceiling.
    /// </summary>
    private static readonly int MaxConcurrentRequests = Math.Max(8, Environment.ProcessorCount * 4);

    public LlmHttpServer(
        LlmServerConfig config,
        MultiModelHost modelHost,
        SessionRegistry sessionRegistry,
        IInferenceScheduler scheduler,
        VramBudget vramBudget,
        IClientManager clientManager,
        ILogger logger,
        CancellationTokenSource? externalCts = null,
        IProcessModelHost? processModelHost = null,
        Engine.Backends.ProcessSessionRegistry? processSessionRegistry = null)
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
            _clientManager, _config, _logger, _cts, processModelHost, processSessionRegistry);

        _requestGate = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);

        _listener.Prefixes.Add(_config.Server.Prefix);
    }

    /// <summary>
    /// Start listening. Blocks until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var cts = _cts ?? throw new InvalidOperationException("CTS not initialized — construct via the primary constructor");
        // Link the per-run token with the constructor-provided _cts — do NOT overwrite it,
        // RequestRouter holds a reference to _cts for shutdown handling.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        _listener.Start();

        // HttpListener.GetContextAsync is not cancellable — without this registration the
        // loop parks in the pending accept forever when only the token is cancelled, and
        // the server survives its own shutdown until an unrelated request unblocks it
        // (observed 2026-09-18/19: 21 h zombie with 854 shutdown re-attempts).
        // Stopping the listener makes the pending GetContextAsync throw and the loop exit.
        using var stopRegistration = runCts.Token.Register(() =>
        {
            try { _listener.Stop(); } catch { /* already stopped/disposed */ }
        });

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

            // Cap concurrent request tasks — pending tasks queue on the gate INSIDE the
            // task body. Acquiring inside Task.Run (not before it) prevents a cancelled
            // task (token already fired) from leaking a semaphore slot: Task.Run with a
            // cancelled token skips the delegate entirely, so a release-before-acquire
            // pair spanning that boundary would never release what it acquired.
            _ = Task.Run(async () =>
            {
                var gateAcquired = false;
                try
                {
                    await _requestGate.WaitAsync(runCts.Token);
                    gateAcquired = true;

                    await _router.RouteAsync(ctx, cts.Token);
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
                    // Suppress noisy client-disconnect errors (broken pipe / aborted connection)
                    // and disposed-response races during shutdown.
                    if (!IsClientDisconnect(ex) && ex is not ObjectDisposedException)
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
                    // Release only what was actually acquired — never a slot we don't hold.
                    if (gateAcquired)
                        _requestGate.Release();
                    try { ctx.Response.Close(); } catch { }
                }
            }, cts.Token);
        }

        _logger.Info("Server", "Shutting down...");
    }

    /// <summary>
    /// True when the exception is an HttpListenerException caused by the client
    /// disconnecting mid-response. Detected via the Win32 error code, not the
    /// (locale-dependent) exception message.
    /// </summary>
    private static bool IsClientDisconnect(Exception ex)
    {
        if (ex is not HttpListenerException hle)
            return false;

        // Win32 error codes indicating client-side disconnect / broken pipe:
        // 32 pipe not connected, 109 broken pipe, 232 no data, 995 operation aborted,
        // 12002 internet timeout, 1229 connection invalid, 1236 connection aborted.
        return hle.ErrorCode is 32 or 109 or 232 or 995 or 12002 or 1229 or 1236;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the listener FIRST so a pending GetContextAsync unblocks with an
        // exception (caught in RunAsync). Cancelling/disposing the CTS before
        // stopping left the accept-loop thread blocked forever → xunit could
        // never finish disposing collection fixtures ("Test Run Aborted").
        try { _listener.Stop(); } catch { }
        // Guarded cancel: the router may be cancelling this same CTS concurrently in
        // HandleShutdownAsync — Cancel on an already-disposed CTS must not crash Dispose.
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* router already cancelled+disposed it */ }
        _cts?.Dispose();

        _clientManager?.Dispose();
        _sessionRegistry?.Dispose();
        _modelHost?.Dispose();
    }
}