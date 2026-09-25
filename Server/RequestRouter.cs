using System.Net;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistantInference.Abstractions;
using ECAssistantInference.Models;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Interfaces;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Routes incoming HTTP requests to the appropriate handler.
/// </summary>
public sealed class RequestRouter : IRequestRouter
{

    private readonly MultiModelHost _models;
    private readonly SessionRegistry _sessions;
    private readonly IInferenceScheduler _scheduler;
    private readonly VramBudget _vram;
    private readonly IClientManager _clients;
    private readonly LlmServerConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly IProcessModelHost? _processHost;
    private readonly ProcessSessionRegistry? _processSessions;
    private readonly ProcessStatelessClient? _processStateless;
    private readonly BackendSelector _backendSelector = new();
    private readonly BatchSessionRegistry? _batchSessions;

    public RequestRouter(
        MultiModelHost models,
        SessionRegistry sessions,
        IInferenceScheduler scheduler,
        VramBudget vram,
        IClientManager clients,
        LlmServerConfig config,
        ILogger logger,
        CancellationTokenSource cts,
        IProcessModelHost? processHost = null,
        ProcessSessionRegistry? processSessions = null,
        BatchSessionRegistry? batchSessions = null)
    {
        _models = models;
        _sessions = sessions;
        _scheduler = scheduler;
        _vram = vram;
        _clients = clients;
        _config = config;
        _logger = logger;
        _cts = cts;
        _processHost = processHost;
        _processSessions = processSessions;
        _processStateless = processHost != null ? new ProcessStatelessClient(processHost) : null;
        _batchSessions = batchSessions;
    }

    public async Task RouteAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        _logger.Debug("Router", $"{method} {path}");

        var clientId = req.Headers["X-Client-Id"];

        // /eca/* management endpoints require a registered client identity.
        // Registration (/eca/clients POST) is the only bootstrap exception.
        bool isEcaManagement = path.StartsWith("/eca/")
                               && path != "/eca/health"
                               && !(path == "/eca/clients" && method == "POST");
        if (isEcaManagement && (string.IsNullOrEmpty(clientId) || !_clients.IsValid(clientId)))
        {
            await SseStreamer.WriteJsonAsync(res,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // OpenAI-compatible inference endpoints require a registered client identity too —
        // same policy as /eca/* management endpoints.
        bool isOpenAiEndpoint = (path == "/v1/chat/completions" && method == "POST")
                             || (path == "/v1/completions" && method == "POST")
                             || (path == "/v1/embeddings" && method == "POST")
                             || (path == "/v1/models" && method == "GET");
        if (isOpenAiEndpoint && (clientId is null || clientId.Length == 0 || !_clients.IsValid(clientId)))
        {
            await WriteInvalidClientAsync(res);
            return;
        }

        // ── OpenAI-compatible endpoints ──
        if (path == "/v1/chat/completions" && method == "POST")
        { await HandleChatCompletionAsync(ctx, clientId!, ct); return; }

        if (path == "/v1/completions" && method == "POST")
        { await HandleCompletionAsync(ctx, clientId!, ct); return; }

        if (path == "/v1/embeddings" && method == "POST")
        { await HandleEmbeddingsAsync(ctx, clientId, ct); return; }

        if (path == "/v1/models" && method == "GET")
        { await HandleListModelsAsync(ctx); return; }

        // ── ECAssistant extension endpoints ──

        // Cross-client authorization: management actions scoped to a client id in the
        // PATH may only be performed by that same client (X-Client-Id header).
        // Without this, registered client A could heartbeat/disconnect client B.
        // /eca/shutdown has no path id — the acting identity IS the validated header.

        // Health
        if (path == "/eca/health" && method == "GET")
        { await HandleHealthAsync(ctx); return; }

        // Client management
        if (path == "/eca/clients" && method == "POST")
        { await HandleRegisterClientAsync(ctx); return; }

        // Heartbeat endpoint REMOVED (Emre 2026-09-23): no client eviction → heartbeats pointless.

        if (path.StartsWith("/eca/clients/") && method == "DELETE")
        {
            var pathClientId = ExtractClientIdFromPath(path);
            if (!MatchesHeaderClient(pathClientId, clientId))
            {
                await SseStreamer.WriteJsonAsync(res,
                    new ErrorResponse { Error = new() { Message = "X-Client-Id header does not match the client in the request path", Type = "forbidden" } }, 403);
                return;
            }
            await HandleDisconnectClientAsync(ctx, pathClientId);
            return;
        }

        // Server shutdown (explicit request from Core)
        if (path == "/eca/shutdown" && method == "POST")
        { await HandleShutdownAsync(ctx, clientId); return; }

        // Session / KV cache
        if (path == "/eca/sessions" && method == "POST")
        {
            // When continuous_batching is enabled, ALL sessions are batch sessions.
            // The outside world sees the same API — the server decides internally.
            if (_batchSessions != null)
                await HandleCreateBatchSessionAsync(ctx, clientId, ct);
            else
                await HandleCreateSessionAsync(ctx, clientId, ct);
            return;
        }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/prefill") && method == "POST")
        { await HandlePrefillOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/prefill"), ct); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/rewind") && method == "POST")
        { await HandleRewindOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/rewind")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/save-state") && method == "POST")
        { await HandleSaveStateOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/save-state")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/reset") && method == "POST")
        { await HandleResetOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/reset")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/evaluate") && method == "POST")
        { await HandleEvaluateOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/evaluate"), ct); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/status") && method == "GET")
        { await HandleSessionStatusOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/status")); return; }

        if (path.StartsWith("/eca/sessions/") && method == "DELETE")
        { await HandleDestroySessionOrBatchAsync(ctx, clientId, ExtractSessionIdFromPath(path, "")); return; }

        // Model management
        if (path == "/eca/models" && method == "GET")
        { await HandleEcaListModelsAsync(ctx); return; }

        if (path == "/eca/models/load" && method == "POST")
        { await HandleLoadModelAsync(ctx); return; }

        if (path == "/eca/models/unload" && method == "POST")
        { await HandleUnloadModelAsync(ctx); return; }

        // Tokenize
        if (path == "/eca/tokenize" && method == "POST")
        { await HandleTokenizeAsync(ctx, clientId); return; }

        // ── 404 ──
        _logger.Warn("Router", $"Not found: {method} {path}");
        await SseStreamer.WriteJsonAsync(res,
            new ErrorResponse { Error = new() { Message = $"Not found: {method} {path}", Type = "not_found" } },
            404);
    }

    // ── Process-backend routing helpers ─────────────────

    /// <summary>True when the model is served by the Process backend (ternary/external llama-server).</summary>
    private bool IsProcessModel(string modelId)
    {
        if (_processHost is null) return false;
        // null = main model (OpenAI default) — resolve it so a process-backed MAIN model
        // routes to the process path instead of the in-process batch/standard path.
        if (modelId is null)
            modelId = _models.MainModelId;
        if (_processHost.Instances.Any(i => i.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
            return true;
        var cfg = _config.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return cfg is not null && _backendSelector.Select(cfg) == ModelBackendKind.Process;
    }

    /// <summary>Ensures the process model is running; returns its base URL (null when unavailable).</summary>
    private async Task<string?> EnsureProcessBaseAsync(string modelId, CancellationToken ct)
    {
        if (_processHost is null) return null;
        var cfg = _config.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (cfg is null) return null;
        var instance = await _processHost.EnsureStartedAsync(cfg, ct);
        return instance.BaseUrl;
    }

    /// <summary>
    /// Stateless-only proxy for Process-backend models (completions/embeddings —
    /// no session support needed on these endpoints). Returns true when handled.
    /// </summary>
    private async Task<bool> TryProxyProcessModelAsync(HttpListenerContext ctx, string? modelId, byte[]? rawBody, CancellationToken ct)
    {
        if (modelId == null || !IsProcessModel(modelId)) return false;

        var baseUrl = await EnsureProcessBaseAsync(modelId, ct);
        if (baseUrl == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
            return true;
        }

        await ProxyRequestHandler.ForwardAsync(ctx, baseUrl, rawBody, ct);
        return true;
    }

    /// <summary>
    /// Chat completions for a Process-backend model. Stateless requests proxy 1:1 to the
    /// child llama-server; session requests run through the transcript-backed
    /// ProcessSession — same client-facing behavior as in-process KV sessions.
    /// </summary>
    private async Task HandleProcessModelChatAsync(HttpListenerContext ctx, ChatCompletionRequest req, string clientId, byte[]? rawBody, CancellationToken ct)
    {
        // v14.8.4: 100% parity — stateless process requests no longer raw-proxy; they run
        // through the same code shapes as in-process stateless (ThinkFilter, same response
        // objects, structured support). Only unhandled paths keep raw 1:1 passthrough.
        if (req.SessionId == null && !req.Structured && !req.ToolsActive)
        {
            // The null-state of the readonly field is re-checked after the validation
            // branches below via the hoisted 'statelessClient' local.
            if (req.Messages.Count == 0)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
                return;
            }

            var maxTokens = Math.Clamp(EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
            if (req.Stream && req.Grammar != null)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "grammar is not supported with stream=true", Type = "invalid_request" } }, 400);
                return;
            }
            var statelessClient = _processStateless; // hoisted: null-state is invalidated by awaited calls above
            if (statelessClient == null)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
                return;
            }
            if (req.Stream)
            {
                var tokenStream = ThinkFilter.ApplyAsync(
                    statelessClient.InferStatelessAsync(req.Model, req.Messages, req, grammar: req.Grammar, maxTokens, ct), ct);
                await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
            }
            else
            {
                var sb = new StringBuilder();
                await foreach (var token in ThinkFilter.ApplyAsync(
                    statelessClient.InferStatelessAsync(req.Model, req.Messages, req, grammar: req.Grammar, maxTokens, ct), ct))
                    sb.Append(token);

                var response = new
                {
                    id = Guid.NewGuid().ToString("N"),
                    @object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = req.Model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = sb.ToString() },
                            finish_reason = "stop"
                        }
                    }
                };
                await SseStreamer.WriteJsonAsync(ctx.Response, response);
            }
            return;
        }

        // Chat is fully handled above for every stateless/session × structured/plain
        // combination — the raw 1:1 proxy is intentionally unreachable here (was the
        // pre-parity escape hatch that silently bypassed ThinkFilter + structured mode).

        // Native OpenAI tools mode (process backend): stateless or session — the child
        // receives the grammar alongside the messages, output is decoded and returned
        // as typed message.tool_calls. Sessions are transcript-backed, so the tool turn
        // appends to the transcript like any other (child prefix-cache stays warm).
        // stream+tools is rejected (tool_calls have no delta representation here).
        if (req.ToolsActive)
        {
            if (req.Messages.Count == 0)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
                return;
            }
            if (req.Stream)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "tools are not supported with stream=true on the process backend", Type = "invalid_request" } }, 400);
                return;
            }

            var tools = req.Tools!.Where(t => t.IsValid).ToList();
            var fingerprint = ToolsetFingerprint.Compute(tools);
            IAsyncEnumerable<string> toolsStream;
            string sessionLabel;
            if (req.SessionId != null)
            {
                var procSession = _processSessions?.Get(clientId, req.SessionId);
                if (procSession == null)
                {
                    await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
                    return;
                }
                // v14.9 pinning: transcript-backed sessions carry ToolsHash like KV
                // sessions; on mismatch we just update — the child's prefix cache
                // rebuilds naturally on the next turn (no explicit KV reset needed).
                var prevHash = procSession.ToolsHash;
                if (prevHash != fingerprint)
                {
                    _logger.Warn("Router", $"[Tools/process] session {procSession.SessionId} toolset changed ({(prevHash ?? "unpinned")[..Math.Min(8, (prevHash ?? "unpinned").Length)]} → {fingerprint[..8]}) — updating pin");
                    procSession.ToolsHash = fingerprint;
                }
                toolsStream = procSession.InferAsync(req.Messages, req, ct, grammar: ToolCallGrammarFactory.Build(tools, _config.Inference.MaxParallelToolCalls));
                sessionLabel = req.SessionId;
            }
            else
            {
                if (_processStateless == null)
                {
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
                    return;
                }
                var toolsMaxTokens = Math.Clamp(EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
                toolsStream = _processStateless.InferStatelessAsync(
                    req.Model, req.Messages, req, grammar: ToolCallGrammarFactory.Build(tools, _config.Inference.MaxParallelToolCalls), toolsMaxTokens, ct);
                sessionLabel = "stateless";
            }

            var toolsSb = new StringBuilder();
            var toolsEarlyStop = false;
            await foreach (var token in toolsStream)
            {
                toolsSb.Append(token);
                if (TryParseCompleteJson(toolsSb.ToString())) { toolsEarlyStop = true; break; }
            }
            _logger.Info("Router", $"[Tools/process] generated {toolsSb.Length} chars (session={sessionLabel}, earlyStop={toolsEarlyStop})");

            try
            {
                var calls = ToolCallDecoder.Decode(toolsSb.ToString(), tools);
                await WriteToolCallsResponseAsync(ctx.Response, req.Model, calls);
            }
            catch (InvalidToolCallException ex)
            {
                _logger.Warn("Router", $"[Tools/process] decode failed: {ex.Message} | raw: {toolsSb.ToString()[..Math.Min(toolsSb.Length, 300)]}");
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = $"Invalid tool_calls output: {ex.Message}", Type = "invalid_tool_calls" } }, 422);
            }
            return;
        }

        // Structured mode — grammar-constrained decision envelope via the child's native
        // GBNF support (grammar + enable_thinking=false). Envelope early-stop mirrors the
        // in-process loop — break the stream as soon as the JSON is complete and valid;
        // disposing the child stream stops the generation.
        if (req.Structured)
        {
            if (req.Messages.Count == 0)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
                return;
            }

            var structuredSw = System.Diagnostics.Stopwatch.StartNew();
            var structuredSb = new StringBuilder();
            var earlyStop = false;
            IAsyncEnumerable<string> structuredStream;
            string sessionIdLabel;
            ProcessSession? structuredSession = null;
            if (req.SessionId != null)
            {
                structuredSession = _processSessions?.Get(clientId, req.SessionId);
                if (structuredSession == null)
                {
                    await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
                    return;
                }
                structuredStream = structuredSession.InferAsync(req.Messages, req, ct, grammar: DecisionGrammar.BuildGbnf(req.ToolNames, _config.Inference.MaxParallelToolCalls));
                sessionIdLabel = req.SessionId;
            }
            else
            {
                // Hoist to a local: field null-state is invalidated by the awaited
                // WriteJsonAsync above, and the compiler can't track it across.
                var stateless = _processStateless;
                if (stateless == null)
                {
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
                    return;
                }
                // v15 (Emre, 2026-09-24): config-driven budget — same rule as the
                // in-process path; the hard 256 clamp truncated long answers.
                var structuredMaxTokens = Math.Clamp(
                    EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
                structuredStream = stateless.InferStatelessAsync(
                    req.Model, req.Messages, req, grammar: DecisionGrammar.BuildGbnf(req.ToolNames, _config.Inference.MaxParallelToolCalls), structuredMaxTokens, ct);
                sessionIdLabel = "stateless";
            }

            _logger.Info("Router", $"[Structured/process] generation start (session={sessionIdLabel}, model={req.Model}, max_tokens={req.MaxTokens})");
            await foreach (var token in structuredStream)
            {
                structuredSb.Append(token);
                if (TryParseCompleteEnvelope(structuredSb.ToString(), out _))
                {
                    earlyStop = true;
                    _logger.Info("Router", $"[Structured/process] early stop at {structuredSb.Length} chars (envelope complete)");
                    break;
                }
            }
            structuredSw.Stop();
            _logger.Info("Router", $"[Structured/process] generated {structuredSb.Length} chars (earlyStop={earlyStop}) in {structuredSw.ElapsedMilliseconds} ms");

            try
            {
                var envelope = StructuredDecoder.Decode(structuredSb.ToString());
                _logger.Info("Router", $"[Structured/process] decoded: answer={envelope.HasAnswer}, toolcalls={envelope.ToolCalls?.Count ?? 0}");
                await SseStreamer.WriteJsonAsync(ctx.Response, new { decision = envelope });
            }
            catch (InvalidDecisionException ex)
            {
                // v15 (Emre, 2026-09-24): gated salvage — same repair as the in-process
                // structured path. The process session is transcript-backed: rewind the
                // truncated turn, then commit the repaired envelope as the assistant
                // reply; the child's prefix cache rebuilds naturally on the next turn.
                DecisionEnvelope? salvaged = null;
                if (!earlyStop)
                    salvaged = EnvelopeSalvager.TryRepair(structuredSb.ToString());
                if (salvaged != null)
                {
                    _logger.Warn("Router", $"[Structured/process] TRUNCATED envelope salvaged: answer={salvaged.Answer?.Length ?? 0} chars (earlyStop={earlyStop}, raw={structuredSb.Length} chars)");
                    if (structuredSession != null)
                    {
                        var rewound = await structuredSession.RewindAsync();
                        if (rewound)
                        {
                            try
                            {
                                var repairedJson = System.Text.Json.JsonSerializer.Serialize(salvaged);
                                structuredSession.AppendAssistantReply(repairedJson);
                            }
                            catch (Exception appendEx)
                            {
                                _logger.Warn("Router", $"[Structured/process] salvage re-append failed: {appendEx.Message}");
                            }
                        }
                        else
                        {
                            _logger.Warn("Router", "[Structured/process] salvage rewind unavailable — keeping raw decode in transcript");
                        }
                    }
                    await SseStreamer.WriteJsonAsync(ctx.Response, new { decision = salvaged });
                }
                else
                {
                    _logger.Warn("Router", $"[Structured/process] decode failed: {ex.Message} | raw: {structuredSb.ToString()[..Math.Min(structuredSb.Length, 300)]}");
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = $"Invalid decision envelope: {ex.Message}", Type = "invalid_decision" } }, 422);
                }
            }
            return;
        }

        // By this point req.SessionId is non-null (both SessionId==null branches returned
        // above), but the compiler can't track it through the awaited branches — assert it.
        var sessionId = req.SessionId!;
        var session = _processSessions?.Get(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        if (req.Messages.Count == 0)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
            return;
        }

        if (req.Stream && req.Grammar != null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "grammar is not supported with stream=true", Type = "invalid_request" } }, 400);
            return;
        }

        if (req.Stream)
        {
            try
            {
                var tokenStream = ThinkFilter.ApplyAsync(session.InferAsync(req.Messages, req, ct, grammar: req.Grammar), ct);
                await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
                return;
            }
        }
        else
        {
            var sb = new StringBuilder();
            try
            {
                await foreach (var token in ThinkFilter.ApplyAsync(session.InferAsync(req.Messages, req, ct, grammar: req.Grammar), ct))
                    sb.Append(token);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
                return;
            }

            var response = new
            {
                id = Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = req.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = sb.ToString() },
                        finish_reason = "stop"
                    }
                }
            };
            await SseStreamer.WriteJsonAsync(ctx.Response, response);
        }
    }

    // ── Continuous batching path ─────────────────────────

    /// <summary>
    /// Chat completions via the shared BatchedExecutor. Used when continuous_batching=true
    /// (all chat completions route through the batch path; sessions resolve from BatchSessionRegistry,
    /// everything else runs on transient sessions). Same response shapes as the standard path.
    /// </summary>
    private async Task HandleBatchChatAsync(HttpListenerContext ctx, ChatCompletionRequest req, string clientId, CancellationToken ct)
    {
        if (_batchSessions == null)
        {
            // Should never reach here — routing checks null before calling. Safety net.
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Batch mode not enabled on server", Type = "invalid_request" } }, 400);
            return;
        }

        if (req.Messages.Count == 0)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
            return;
        }

        var templateSlot = _models.TryGetSlot(req.Model) ?? _models.TryGetSlot(_models.MainModelId);
        if (templateSlot?.Model == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }

        var builtPrompt = BuildPromptFromMessages(templateSlot, req.Messages);
        var inferenceParams = CreateInferenceParams(req);

        // Resolve or create a batch session
        BatchSession? batchSession = null;
        if (req.SessionId != null)
        {
            batchSession = _batchSessions.GetSession(clientId, req.SessionId);
            if (batchSession == null)
            {
                await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
                return;
            }
        }

        // Native tools mode on the batch path
        if (req.ToolsActive)
        {
            var tools = req.Tools!.Where(t => t.IsValid).ToList();
            var fingerprint = ToolsetFingerprint.Compute(tools);

            // ToolsetFingerprint check — same as standard path: reset on mismatch
            if (batchSession != null && batchSession.ToolsHash != fingerprint)
            {
                if (batchSession.ToolsHash != null)
                {
                    _logger.Warn("Router", $"[Tools/batch] session {batchSession.SessionId} toolset changed ({batchSession.ToolsHash[..Math.Min(8, batchSession.ToolsHash.Length)]} → {fingerprint[..8]}) — resetting KV cache");
                    try { await batchSession.ResetAsync(); }
                    catch (Exception ex)
                    {
                        _logger.Error("Router", $"[Tools/batch] cache reset failed: {ex.Message}");
                        await SseStreamer.WriteJsonAsync(ctx.Response,
                            new ErrorResponse { Error = new() { Message = $"Toolset changed and KV cache reset failed: {ex.Message}", Type = "model_error" } }, 503);
                        return;
                    }
                }
                else
                {
                    _logger.Info("Router", $"[Tools/batch] session {batchSession.SessionId} adopting toolset {fingerprint[..8]}");
                }
                batchSession.ToolsHash = fingerprint;
            }

            var grammar = ToolCallGrammarFactory.Build(tools, _config.Inference.MaxParallelToolCalls);
            var toolsParams = CreateGrammarInferenceParams(grammar, req.Temperature, req.TopP, req.TopK, req.RepeatPenalty,
                Math.Clamp(EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens));

            var toolsSb = new StringBuilder();
            var toolsEarlyStop = false;

            if (batchSession != null)
            {
                await foreach (var token in batchSession.InferAsync(builtPrompt, toolsParams, ct, grammarStr: ToolCallGrammarFactory.Build(req.Tools!, _config.Inference.MaxParallelToolCalls), grammarRoot: ToolCallGrammarFactory.Root))
                {
                    toolsSb.Append(token);
                    if (TryParseCompleteJson(toolsSb.ToString())) { toolsEarlyStop = true; break; }
                }
            }
            else
            {
                // Stateless batch: create transient session, infer, dispose
                var transientId = $"_transient_{Guid.NewGuid():N}";
                var transient = _batchSessions.CreateSession(clientId, transientId, req.Model);
                try
                {
                    await foreach (var token in transient.InferAsync(builtPrompt, toolsParams, ct,
                        grammarStr: ToolCallGrammarFactory.Build(req.Tools!, _config.Inference.MaxParallelToolCalls), grammarRoot: ToolCallGrammarFactory.Root))
                    {
                        toolsSb.Append(token);
                        if (TryParseCompleteJson(toolsSb.ToString())) { toolsEarlyStop = true; break; }
                    }
                }
                finally { _batchSessions.DestroySession(clientId, transientId); }
            }

            _logger.Info("Router", $"[Tools/batch] generated {toolsSb.Length} chars (earlyStop={toolsEarlyStop})");
            try
            {
                var calls = ToolCallDecoder.Decode(toolsSb.ToString(), tools);
                await WriteToolCallsResponseAsync(ctx.Response, req.Model, calls);
            }
            catch (InvalidToolCallException ex)
            {
                _logger.Warn("Router", $"[Tools/batch] decode failed: {ex.Message} | raw: {toolsSb.ToString()[..Math.Min(toolsSb.Length, 400)]}");
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = $"Invalid tool_calls output: {ex.Message}", Type = "invalid_tool_calls" } }, 422);
            }
            return;
        }

        // Structured mode on the batch path
        if (req.Structured)
        {
            var structuredMaxTokens = Math.Clamp(
                EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
            var structuredParams = CreateStructuredInferenceParams(req, structuredMaxTokens);
            var structuredSb = new StringBuilder();
            var earlyStop = false;

            if (batchSession != null)
            {
                await foreach (var token in batchSession.InferAsync(builtPrompt, structuredParams, ct, grammarStr: DecisionGrammar.BuildGbnf(req.ToolNames, _config.Inference.MaxParallelToolCalls), grammarRoot: DecisionGrammar.Root))
                {
                    structuredSb.Append(token);
                    if (TryParseCompleteEnvelope(structuredSb.ToString(), out var earlyEnvelope))
                    {
                        earlyStop = true;
                        break;
                    }
                }
            }
            else
            {
                var transientId = $"_transient_{Guid.NewGuid():N}";
                var transient = _batchSessions.CreateSession(clientId, transientId, req.Model);
                try
                {
                    await foreach (var token in transient.InferAsync(builtPrompt, structuredParams, ct))
                    {
                        structuredSb.Append(token);
                        if (TryParseCompleteEnvelope(structuredSb.ToString(), out var earlyEnvelope))
                        { earlyStop = true; break; }
                    }
                }
                finally { _batchSessions.DestroySession(clientId, transientId); }
            }

            _logger.Info("Router", $"[Structured/batch] generated {structuredSb.Length} chars (earlyStop={earlyStop})");
            try
            {
                var decision = StructuredDecoder.Decode(structuredSb.ToString());
                await SseStreamer.WriteJsonAsync(ctx.Response, new { decision });
            }
            catch (InvalidDecisionException ex)
            {
                var salvaged = EnvelopeSalvager.TryRepair(structuredSb.ToString());
                if (salvaged != null)
                {
                    _logger.Info("Router", $"[Structured/batch] salvage succeeded");
                    await SseStreamer.WriteJsonAsync(ctx.Response, new { decision = salvaged });
                }
                else
                {
                    _logger.Warn("Router", $"[Structured/batch] decode failed: {ex.Message}");
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = $"Invalid decision output: {ex.Message}", Type = "invalid_decision" } }, 422);
                }
            }
            return;
        }

        // Plain chat (streaming or non-streaming) on the batch path
        if (req.Stream)
        {
            IAsyncEnumerable<string> tokenStream;
            if (batchSession != null)
            {
                tokenStream = batchSession.InferAsync(builtPrompt, inferenceParams, ct);
            }
            else
            {
                var transientId = $"_transient_{Guid.NewGuid():N}";
                var transient = _batchSessions.CreateSession(clientId, transientId, req.Model);
                tokenStream = WithCleanup(transient.InferAsync(builtPrompt, inferenceParams, ct),
                    () => _batchSessions.DestroySession(clientId, transientId));
            }
            await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();
            if (batchSession != null)
            {
                await foreach (var token in batchSession.InferAsync(builtPrompt, inferenceParams, ct))
                    sb.Append(token);
            }
            else
            {
                var transientId = $"_transient_{Guid.NewGuid():N}";
                var transient = _batchSessions.CreateSession(clientId, transientId, req.Model);
                try
                {
                    await foreach (var token in transient.InferAsync(builtPrompt, inferenceParams, ct))
                        sb.Append(token);
                }
                finally { _batchSessions.DestroySession(clientId, transientId); }
            }

            var response = new
            {
                id = Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = req.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = sb.ToString() },
                        finish_reason = "stop"
                    }
                }
            };
            await SseStreamer.WriteJsonAsync(ctx.Response, response);
        }
    }

    /// <summary>Wraps an async enumerable with a cleanup action that runs after enumeration completes (success or cancellation).</summary>
    private static async IAsyncEnumerable<string> WithCleanup(IAsyncEnumerable<string> source, Action cleanup)
    {
        try { await foreach (var item in source) yield return item; }
        finally { cleanup(); }
    }

    private async Task HandleChatCompletionAsync(HttpListenerContext ctx, string clientId, CancellationToken ct)
    {
        var (req, ok, rawBody) = await ReadBodyOrErrorAsync<ChatCompletionRequest>(ctx, ct);
        if (!ok || req is null) return; // tuple: compiler can't narrow req from ok alone

        // M-10: a JSON null model field deserializes to null even though the DTO
        // default is "main" — TryGetSlot(null) would throw (500). Return 400 instead.
        if (string.IsNullOrWhiteSpace(req.Model))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "model is required", Type = "invalid_request" } }, 400);
            return;
        }

        // Process-backend models (ternary): stateless → 1:1 proxy; with session_id →
        // transcript-backed ProcessSession (identical client-facing behavior to KV sessions).
        if (IsProcessModel(req.Model))
        { await HandleProcessModelChatAsync(ctx, req, clientId, rawBody, ct); return; }

        // Continuous batching path: when enabled in config, ALL inference routes through
        // BatchedExecutor. When disabled (default), the standard SessionContext path is used.
        // The outside world sees identical API either way — the server decides internally.
        if (_batchSessions != null)
        { await HandleBatchChatAsync(ctx, req, clientId, ct); return; }

        var session = ResolveSession(clientId, req.SessionId);
        if (session == null && req.SessionId != null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
            return;
        }

        var templateSlot = _models.TryGetSlot(req.Model) ?? _models.TryGetSlot(_models.MainModelId);
        if (templateSlot?.Model == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }
        var prompt = BuildPromptFromMessages(templateSlot, req.Messages);
        var inferenceParams = CreateInferenceParams(req);
        // v15: clamp generation length to the session's REMAINING context so a
        // single oversized turn can't run past the wall on shift-incapable models
        // (the executor throws ContextOverflowed when it tries to truncate in place).
        ClampToSessionHeadroom(session, inferenceParams, structured: false);

        // Vision: collect image payloads from all messages (markers are already in Content).
        var images = req.Messages.Where(m => m.HasImages).SelectMany(m => m.Images).Select(i => i.Data).ToList();

        if (images.Count > 0)
        {
            // Validate vision support against the model that will actually run:
            // with a session the tokens flow through session.ModelId's executor,
            // not the template slot. This single check covers both the structured
            // and the streaming/non-streaming paths below.
            var visionSlot = session != null ? _models.TryGetSlot(session.ModelId) : templateSlot;
            if (visionSlot == null || !visionSlot.SupportsVision)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "Model does not support vision — configure mmproj_path for this model.", Type = "invalid_request" } }, 400);
                return;
            }
        }

        await using var slot = await _scheduler.AcquireAsync(ct);

        // Native OpenAI tools mode: grammar-forced tool_calls generation, typed response.
        if (req.ToolsActive)
        {
            await HandleNativeToolsChatAsync(ctx, req, session, templateSlot, prompt, images, ct);
            return;
        }

        // v13 structured mode: grammar-forced decision envelope, parsed server-side.
        // Always non-streamed — the client gets one JSON document with the decision.
        if (req.Structured)
        {
            var structuredSw = System.Diagnostics.Stopwatch.StartNew();
            _logger.Info("Router", $"[Structured] generation start (session={req.SessionId ?? "stateless"}, max_tokens={req.MaxTokens})");
            _logger.Debug("Router", $"[Structured] path: {(session != null ? "session" : "stateless")}, grammar: {(req.ToolNames != null ? "toolcall" : "answer")}");
            // v15 (Emre, 2026-09-24): the structured envelope budget is config-driven —
            // request value wins, then per-model catalog, then the global inference
            // default. The old hard 256 clamp contradicted the v15 max_tokens law
            // ("no tier caps, config-driven budgets") and truncated legitimate long
            // answers mid-envelope; early-stop already bounds normal generations.
            var structuredMaxTokens = Math.Clamp(
                EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
            var structuredParams = CreateStructuredInferenceParams(req, structuredMaxTokens);
            // v15: same headroom clamp for the structured session path (see below).
            if (session != null) structuredParams = ClampToSessionHeadroom(session, structuredParams, structured: true);
            var structuredSb = new StringBuilder();
            var earlyStop = false;
            try
            {
            if (session != null)
            {
                await foreach (var token in session.InferAsync(prompt, structuredParams, ct, images, DecisionGrammar.BuildGbnf(req.ToolNames, _config.Inference.MaxParallelToolCalls), DecisionGrammar.Root))
                {
                    structuredSb.Append(token);
                    // v14.7: Early termination — stop generation as soon as we have
                    // a complete, valid JSON envelope. The grammar allows the root
                    // to match, but LLamaSharp may keep sampling tokens afterward
                    // (up to max_tokens). Checking brace balance + attempting parse
                    // saves ~70% of generation time on simple decisions.
                    if (TryParseCompleteEnvelope(structuredSb.ToString(), out var earlyEnvelope))
                    {
                        earlyStop = true;
                        _logger.Info("Router", $"[Structured] early stop at {structuredSb.Length} chars (envelope complete)");
                        break;
                    }
                }
            }
            else
            {
                await foreach (var token in CreateStatelessStream(templateSlot, prompt, structuredParams, images, ct,
                    DecisionGrammar.BuildGbnf(req.ToolNames, _config.Inference.MaxParallelToolCalls), DecisionGrammar.Root))
                {
                    structuredSb.Append(token);
                    if (TryParseCompleteEnvelope(structuredSb.ToString(), out var earlyEnvelope))
                    {
                        earlyStop = true;
                        _logger.Info("Router", $"[Structured] early stop at {structuredSb.Length} chars (envelope complete)");
                        break;
                    }
                }
            }
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
                return;
            }

            structuredSw.Stop();
            _logger.Info("Router", $"[Structured] generated {structuredSb.Length} chars (earlyStop={earlyStop}) in {structuredSw.ElapsedMilliseconds} ms");
            try
            {
                var envelope = StructuredDecoder.Decode(structuredSb.ToString());
                _logger.Info("Router", $"[Structured] decoded: answer={envelope.HasAnswer}, toolcalls={envelope.ToolCalls?.Count ?? 0}");
                _logger.Info("Router", $"[Structured] RAW OUTPUT ({structuredSb.Length} chars): {structuredSb.ToString()[..Math.Min(structuredSb.Length, 500)]}");
                if (envelope.HasAnswer) _logger.Info("Router", $"[Structured] ANSWER TEXT: {envelope.Answer?[..Math.Min(envelope.Answer.Length, 200)]}");
                await SseStreamer.WriteJsonAsync(ctx.Response, new { decision = envelope });
            }
            catch (InvalidDecisionException ex)
            {
                // v15 (Emre, 2026-09-24): gated salvage — a truncated envelope (budget
                // burned inside the answer string, earlyStop never fired) still holds
                // the complete content in the buffer. Repair it and return a valid
                // decision instead of failing the turn into the empty-retry cycle.
                DecisionEnvelope? salvaged = null;
                if (!earlyStop)
                    salvaged = EnvelopeSalvager.TryRepair(structuredSb.ToString());
                if (salvaged != null)
                {
                    _logger.Warn("Router", $"[Structured] TRUNCATED envelope salvaged: answer={salvaged.Answer?.Length ?? 0} chars (earlyStop={earlyStop}, raw={structuredSb.Length} chars)");
                    // KV hygiene (session path only): rewind past the truncated decode,
                    // then re-feed the repaired envelope so the cache holds a clean,
                    // well-formed example instead of an unterminated one. MaxTokens=1
                    // bounds the post-prefill sample to a single (harmless) token.
                    if (session != null)
                    {
                        var rewound = await session.RewindAsync();
                        if (rewound)
                        {
                            try
                            {
                                var repairedJson = System.Text.Json.JsonSerializer.Serialize(salvaged);
                                // First-class KV-hygiene primitive: prompt-only feed with
                                // bounded sampling (≤1 stray sample token, discarded).
                                var (fed, _, _) = await session.EvaluateAsync(repairedJson, maxTokens: 1, ct);
                                if (!fed)
                                    _logger.Warn("Router", "[Structured] salvage KV refeed rejected by session");
                            }
                            catch (Exception feedEx)
                            {
                                _logger.Warn("Router", $"[Structured] salvage KV refeed failed: {feedEx.Message}");
                            }
                        }
                        else
                        {
                            _logger.Warn("Router", "[Structured] salvage rewind unavailable — keeping raw decode in cache");
                        }
                    }
                    await SseStreamer.WriteJsonAsync(ctx.Response, new { decision = salvaged });
                }
                else
                {
                    _logger.Warn("Router", $"Structured decode failed: {ex.Message} | raw: {structuredSb.ToString()[..Math.Min(structuredSb.Length, 300)]}");
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = $"Invalid decision envelope: {ex.Message}", Type = "invalid_decision" } }, 422);
                }
            }
            return;
        }

        if (req.Stream)
        {
            // v15: overflow-guarded — shift-incapable models throw on ANY KV touch;
            // without this catch the failure surfaces as a bare 500 and the session dies.
            try
            {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, images, req.Grammar), req.Stop, ct), ct);
            }
            else
            {
                tokenStream = ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(CreateStatelessStream(templateSlot, prompt, inferenceParams, images, ct), req.Stop, ct), ct);
            }

            await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
            }
        }
        else
        {
            var sb = new StringBuilder();
            try
            {
            if (session != null)
            {
                await foreach (var token in ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, images, req.Grammar), req.Stop, ct), ct))
                {
                    sb.Append(token);
                    if (req.Grammar != null && TryParseCompleteJson(sb.ToString())) break;
                }
            }
            else
            {
                await foreach (var token in ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(CreateStatelessStream(templateSlot, prompt, inferenceParams, images, ct), req.Stop, ct), ct))
                {
                    sb.Append(token);
                    if (req.Grammar != null && TryParseCompleteJson(sb.ToString())) break;
                }
            }
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
                return;
            }

            var response = new
            {
                id = Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = req.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = sb.ToString() },
                        finish_reason = "stop"
                    }
                }
            };
            await SseStreamer.WriteJsonAsync(ctx.Response, response);
        }
    }

    private async Task HandleCompletionAsync(HttpListenerContext ctx, string clientId, CancellationToken ct)
    {
        var (req, ok, rawBody) = await ReadBodyOrErrorAsync<CompletionRequest>(ctx, ct);
        if (!ok || req is null) return; // tuple: compiler can't narrow req from ok alone

        // M-10: guard null model fields — 400, not a 500 from TryGetSlot(null).
        if (string.IsNullOrWhiteSpace(req.Model))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "model is required", Type = "invalid_request" } }, 400);
            return;
        }

        if (await TryProxyProcessModelAsync(ctx, req.Model, rawBody, ct)) return;

        if (string.IsNullOrEmpty(req.Prompt))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "prompt is required", Type = "invalid_request" } }, 400);
            return;
        }

        var session = ResolveSession(clientId, req.SessionId);
        if (session == null && req.SessionId != null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
            return;
        }

        var slot = _models.TryGetSlot(req.Model) ?? _models.TryGetSlot(_models.MainModelId);
        if (slot?.Model == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }
        var prompt = req.Prompt;

        var inferenceParams = CreateInferenceParams(req);
        // v15: same headroom clamp for /v1/completions session requests.
        ClampToSessionHeadroom(session, inferenceParams, structured: false);

        await using var gate = await _scheduler.AcquireAsync(ct);

        if (req.Stream)
        {
            // v15: overflow-guarded (same rationale as the chat path).
            try
            {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, grammarStr: null), req.Stop, ct), ct);
            }
            else
            {
                tokenStream = ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(CreateStatelessStream(slot, prompt, inferenceParams, images: null, ct), req.Stop, ct), ct);
            }

            await SseStreamer.StreamCompletionAsync(ctx.Response, tokenStream, req.Model, ct);
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
            }
        }
        else
        {
            var sb = new StringBuilder();
            try
            {
            if (session != null)
            {
                await foreach (var token in ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, grammarStr: null), req.Stop, ct), ct))
                    sb.Append(token);
            }
            else
            {
                await foreach (var token in ThinkFilter.ApplyAsync(StopFilter.ApplyAsync(CreateStatelessStream(slot, prompt, inferenceParams, images: null, ct), req.Stop, ct), ct))
                    sb.Append(token);
            }
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow(session);
                await WriteContextOverflowResponseAsync(ctx);
                return;
            }

            var response = new CompletionResponse
            {
                Id = Guid.NewGuid().ToString("N"),
                Model = req.Model,
                Choices = new()
                {
                    new CompletionChoice
                    {
                        Text = sb.ToString(),
                        Index = 0,
                        FinishReason = "stop"
                    }
                }
            };
            await SseStreamer.WriteJsonAsync(ctx.Response, response);
        }
    }

    private async Task HandleEmbeddingsAsync(HttpListenerContext ctx, string? clientId, CancellationToken ct)
    {
        var req = await SseStreamer.ReadJsonAsync<EmbeddingRequest>(ctx.Request, ct);
        if (req == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Invalid request", Type = "invalid_request" } }, 400);
            return;
        }

        if (string.IsNullOrEmpty(req.Input))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "input is required", Type = "invalid_request" } }, 400);
            return;
        }

        // JSON null overrides the DTO default — null-guard before TryGetSlot (M-10).
        if (req.Model != null && await TryProxyProcessModelAsync(ctx, req.Model, null, ct)) return;
        var slot = (req.Model != null ? _models.TryGetSlot(req.Model) : null) ?? _models.GetEmbeddingSlot();
        if (slot == null || !slot.IsEmbedding || slot.Model == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Embedding model not available", Type = "model_error" } }, 503);
            return;
        }

        try
        {
            using var embCtx = slot.Model.CreateContext(SessionRegistry.CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers));
            var embedding = slot.Model.GetEmbeddings(embCtx, req.Input);
            var response = new EmbeddingResponse
            {
                Model = slot.Id,
                Data = new List<EmbeddingData>
                {
                    new() { Embedding = embedding, Index = 0 }
                }
            };
            await SseStreamer.WriteJsonAsync(ctx.Response, response);
        }
        catch (Exception ex)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = ex.Message, Type = "embedding_error" } }, 500);
        }
    }

    private async Task HandleListModelsAsync(HttpListenerContext ctx)
    {
        var models = _models.GetModelInfoList();
        var entries = models.Select(m => new
        {
            id = m.Id,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "ecassistant",
            loaded = m.IsLoaded,
            is_embedding = m.IsEmbedding
        }).ToList();

        // Process-backend (ternary) models appear as their own entries.
        if (_processHost is not null)
        {
            foreach (var cfg in _config.Models)
            {
                if (IsProcessModel(cfg.Id) && !entries.Any(e => e.id == cfg.Id))
                {
                    entries.Add(new
                    {
                        id = cfg.Id,
                        @object = "model",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        owned_by = "ecassistant",
                        loaded = _processHost.Instances.Any(i => i.ModelId.Equals(cfg.Id, StringComparison.OrdinalIgnoreCase)),
                        is_embedding = cfg.IsEmbedding
                    });
                }
            }
        }

        var response = new { @object = "list", data = entries };
        await SseStreamer.WriteJsonAsync(ctx.Response, response);
    }

    // ── ECAssistant handlers ─────────────────────────────

    private async Task HandleHealthAsync(HttpListenerContext ctx)
    {
        var response = new
        {
            status = "ok",
            version = LlmServerInfo.Version,
            models_loaded = _models.LoadedModelIds,
            sessions = _sessions.Count,
            clients = _clients.ClientCount,
            vision = _models.LoadedModelIds.Select(id => _models.TryGetSlot(id)).Any(s => s?.SupportsVision == true),
            // v13c capability advertisement — clients negotiate features at connect.
            capabilities = new[] { "eca-extensions", "structured-decoding", "openai-tools" },
            uptime_sec = (int)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
        };
        await SseStreamer.WriteJsonAsync(ctx.Response, response);
    }

    private async Task HandleRegisterClientAsync(HttpListenerContext ctx)
    {
        var req = await SseStreamer.ReadJsonAsync<ClientRegisterRequest>(ctx.Request);
        if (req == null || string.IsNullOrWhiteSpace(req.ClientName))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "client_name required", Type = "invalid_request" } }, 400);
            return;
        }

        var clientId = _clients.Register(req.ClientName, req.Version);
        await SseStreamer.WriteJsonAsync(ctx.Response, new ClientRegisterResponse
        {
            ClientId = clientId,
            ServerVersion = LlmServerInfo.Version
        });
    }

    private async Task HandleDisconnectClientAsync(HttpListenerContext ctx, string clientId)
    {
        if (!_clients.Disconnect(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Unknown client", Type = "client_not_found" } }, 404);
            return;
        }

        await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Message = "Disconnected" });
    }

    /// <summary>
    /// Handle explicit shutdown request from a client.
    /// Disconnects the client, then triggers server shutdown if this was the last client.
    /// The server will wind down gracefully after responding.
    /// </summary>
    private async Task HandleShutdownAsync(HttpListenerContext ctx, string? clientId)
    {
        _logger.Info("Router", $"Shutdown requested by client {clientId}");

        // Disconnect the requesting client first
        if (!string.IsNullOrEmpty(clientId))
            _clients.Disconnect(clientId);

        // Respond OK and flush it to the wire BEFORE cancelling the CTS — otherwise the
        // listener could tear the connection down mid-write and the client never sees the ack.
        await SseStreamer.WriteJsonAsync(ctx.Response,
            new SuccessResponse { Message = "Shutting down" });
        try { await ctx.Response.OutputStream.FlushAsync(); } catch { /* response may already be gone */ }

        // If no clients remain, trigger shutdown
        if (_clients.ClientCount == 0)
        {
            _logger.Info("Router", "Last client shutdown — winding down server...");
            // Small delay to let the response flush
            await Task.Delay(100);
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* server already tearing down */ }
        }
        else
        {
            _logger.Info("Router", $"Shutdown skipped — {_clients.ClientCount} client(s) still connected");
        }
    }

    private async Task HandleCreateSessionAsync(HttpListenerContext ctx, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        var req = await SseStreamer.ReadJsonAsync<CreateSessionRequest>(ctx.Request, ct);
        if (req == null || string.IsNullOrEmpty(req.SessionId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "session_id required", Type = "invalid_request" } }, 400);
            return;
        }

        try
        {
            // Model id may be absent (legacy clients send only session_id). Resolve the
            // default (main) model so process-backend mains still get a process session —
            // the in-process registry cannot serve them (no slot) and would 400, leaving
            // every later prefill/chat to 404 with session_not_found.
            var requestedModelId = string.IsNullOrWhiteSpace(req.ModelId)
                ? _models.MainModelId
                : req.ModelId;

            // Unknown model id (stale client config, e.g. pre-catalog default "main"):
            // resolve to the server's main model instead of hard-failing — the client
            // wants its configured chat model and the server knows best what that is.
            var requestModelExists = _config.Models.Any(m => m.Id.Equals(requestedModelId, StringComparison.OrdinalIgnoreCase));
            if (!requestModelExists)
            {
                _logger.Warn("Router", $"CreateSession: model '{requestedModelId}' not configured — falling back to main model '{_models.MainModelId}'");
                requestedModelId = _models.MainModelId;
            }

            // Process-backend models get transcript-backed sessions in their own registry
            // (no VramBudget — KV lives in the child process), identical response shape.
            var processModelCfg = _config.Models.FirstOrDefault(m => m.Id.Equals(requestedModelId, StringComparison.OrdinalIgnoreCase) && IsProcessModel(m.Id));
            if (processModelCfg != null)
            {
                if (_processSessions == null)
                    throw new InvalidOperationException("Process backend unavailable");
                var procSession = _processSessions.Create(clientId, req.SessionId, processModelCfg);
                await SseStreamer.WriteJsonAsync(ctx.Response, new
                {
                    session_id = procSession.SessionId,
                    client_id = procSession.ClientId,
                    model_id = procSession.ModelId,
                    context_size = procSession.ContextSize,
                    estimated_vram_mb = procSession.EstimatedVramMb
                });
                return;
            }

            // VramBudget reservation + release happen inside SessionRegistry (symmetric accounting)
            // v14.10.1: pass the RESOLVED model id — with the raw id an unknown/
            // foreign model (e.g. a client that switched from remote to local but still
            // sends its cloud model_id) falls back in the handler above, yet the raw id
            // reached the registry, threw, and surfaced as 400 → the client saw every
            // CreateSession fail and dropped to a context-free degraded mode.
            var session = _sessions.CreateSession(clientId, req.SessionId, requestedModelId, req.ToolsHash);

            await SseStreamer.WriteJsonAsync(ctx.Response, new
            {
                session_id = session.SessionId,
                client_id = session.ClientId,
                tools_hash = session.ToolsHash,
                model_id = session.ModelId,
                context_size = session.ContextSize,
                estimated_vram_mb = session.EstimatedVramMb
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("VRAM budget exceeded"))
        {
            // Documented contract: over-budget session creation is a 503, not a client error.
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = ex.Message, Type = "vram_exceeded" } }, 503);
        }
        catch (Exception ex)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = ex.Message, Type = "session_error" } }, 400);
        }
    }

    private async Task HandlePrefillAsync(HttpListenerContext ctx, string? clientId, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        var req = await SseStreamer.ReadJsonAsync<PrefillRequest>(ctx.Request, ct);
        if (req == null || string.IsNullOrEmpty(req.Text))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            try
            {
                var (psuccess, ptokens, pelapsedMs) = await procSession.PrefillAsync(req.Text, ct);
                await SseStreamer.WriteJsonAsync(ctx.Response, new PrefillResponse
                {
                    Prefilled = psuccess,
                    Tokens = ptokens,
                    ElapsedMs = pelapsedMs
                });
            }
            catch (Exception ex) when (IsContextOverflow(ex))
            {
                RecoverFromContextOverflow((ProcessSession?)null);
                procSession.Reset();
                await WriteContextOverflowResponseAsync(ctx);
            }
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        try
        {
            var (success, tokens, elapsedMs) = await session.PrefillAsync(req.Text, ct);
            await SseStreamer.WriteJsonAsync(ctx.Response, new PrefillResponse
            {
                Prefilled = success,
                Tokens = tokens,
                ElapsedMs = elapsedMs
            });
        }
        catch (Exception ex) when (IsContextOverflow(ex))
        {
            // Shift-incapable models throw instead of truncating. Reset the KV cache
            // (the session itself stays alive) and tell the client to compact + retry.
            RecoverFromContextOverflow(session);
            await WriteContextOverflowResponseAsync(ctx);
        }
    }

    private async Task HandleRewindAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            var procOk = await procSession.RewindAsync();
            await SseStreamer.WriteJsonAsync(ctx.Response, new RewindResponse { Rewound = procOk });
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        var ok = await session.RewindAsync();
        await SseStreamer.WriteJsonAsync(ctx.Response, new RewindResponse { Rewound = ok });
    }

    private async Task HandleSaveStateAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            var procOk = procSession.SaveState();
            await SseStreamer.WriteJsonAsync(ctx.Response, new { saved = procOk });
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        var ok = session.SaveState();
        await SseStreamer.WriteJsonAsync(ctx.Response, new { saved = ok });
    }

    /// <summary>
    /// v15 (Emre, 2026-09-24): POST /eca/sessions/{id}/evaluate — KV-hygiene
    /// primitive. Feeds text into the session's cache as prompt with bounded
    /// sampling (≤ max_tokens, default 1, response discarded) without committing a
    /// conversation turn. Use cases: repaired-content injection after a rewind
    /// (envelope salvage follow-up), cache warming of injected context, steering
    /// content placement. Process sessions: transient child turn, transcript
    /// untouched. In-process sessions: prompt consumed by the executor directly.
    /// </summary>
    private async Task HandleEvaluateAsync(HttpListenerContext ctx, string? clientId, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        var req = await SseStreamer.ReadJsonAsync<EvaluateRequest>(ctx.Request);
        if (req == null || string.IsNullOrEmpty(req.Text))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            var (pOk, pSampled) = await procSession.EvaluateAsync(req.Text, req.MaxTokens ?? 1, ct);
            await SseStreamer.WriteJsonAsync(ctx.Response, new EvaluateResponse
            {
                Accepted = pOk,
                PromptChars = req.Text.Length,
                SampledTokens = pSampled,
                ApproxTokens = procSession.ApproxTokenCount,
            });
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        var (ok, sampled, approx) = await session.EvaluateAsync(req.Text, req.MaxTokens ?? 1, ct);
        await SseStreamer.WriteJsonAsync(ctx.Response, new EvaluateResponse
        {
            Accepted = ok,
            PromptChars = req.Text.Length,
            SampledTokens = sampled,
            ApproxTokens = approx,
        });
    }

    private async Task HandleResetAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            try
            {
                procSession.Reset();
                await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Message = "Transcript reset" });
            }
            catch (TimeoutException tex)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = tex.Message, Type = "session_busy" } }, 409);
            }
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        try
        {
            session.Reset();
            await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Message = "KV cache reset" });
        }
        catch (TimeoutException tex)
        {
            // Reset could not acquire the IO lock within its bounded wait — an inference
            // is still running. Not a server fault: surface as a client-retryable conflict.
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = tex.Message, Type = "session_busy" } }, 409);
        }
    }

    private async Task HandleSessionStatusAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var procSession = _processSessions?.Get(clientId, sessionId);
        if (procSession != null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response, new
            {
                session_id = procSession.SessionId,
                client_id = procSession.ClientId,
                model_id = procSession.ModelId,
                is_prefilled = procSession.IsPrefilled,
                has_saved_state = procSession.HasSavedState,
                approx_tokens = procSession.ApproxTokenCount,
                context_size = procSession.ContextSize,
                headroom_tokens = (long)procSession.ContextSize - Math.Min((long)procSession.ApproxTokenCount, (long)procSession.ContextSize),
                estimated_vram_mb = procSession.EstimatedVramMb,
                created_at = procSession.CreatedAt,
                last_activity = procSession.LastActivity
            });
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        await SseStreamer.WriteJsonAsync(ctx.Response, new
        {
            session_id = session.SessionId,
            client_id = session.ClientId,
            model_id = session.ModelId,
            is_prefilled = session.IsPrefilled,
            has_saved_state = session.HasSavedState,
            approx_tokens = session.ApproxTokenCount,
            context_size = session.ContextSize,
            headroom_tokens = (long)session.ContextSize - Math.Min((long)session.ApproxTokenCount, (long)session.ContextSize),
            estimated_vram_mb = session.EstimatedVramMb,
            created_at = session.CreatedAt,
            last_activity = session.LastActivity
        });
    }

    private async Task HandleDestroySessionAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        // Process sessions first (transcript-backed), then in-process KV sessions.
        var ok = _processSessions?.Destroy(clientId, sessionId) ?? false;
        if (!ok) ok = _sessions.DestroySession(clientId, sessionId);
        await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Ok = ok, Message = ok ? "Destroyed" : "Not found" });
    }

    private async Task HandleEcaListModelsAsync(HttpListenerContext ctx)
    {
        var models = _models.GetModelInfoList();
        await SseStreamer.WriteJsonAsync(ctx.Response, new { models });
    }

    private async Task HandleLoadModelAsync(HttpListenerContext ctx)
    {
        var req = await SseStreamer.ReadJsonAsync<LoadModelRequest>(ctx.Request);
        if (req == null || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Path))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "id and path required", Type = "invalid_request" } }, 400);
            return;
        }

        if (!IsAllowedModelPath(req.Path, _config.Server.ModelsRoot))
        {
            _logger.Warn("Router", $"Blocked model load outside models_root: {req.Path}");
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model path is outside the configured models_root", Type = "forbidden" } }, 403);
            return;
        }

        var modelConfig = new Config.ModelConfig
        {
            Id = req.Id,
            Path = req.Path,
            GpuLayers = req.GpuLayers,
            ContextSize = req.ContextSize,
            Threads = req.Threads,
            IsEmbedding = req.IsEmbedding
        };

        var ok = await _models.TryLoadModelAsync(modelConfig);
        if (ok)
            await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Ok = true, Message = "Loaded" });
        else
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Failed to load model — check path and model file", Type = "model_error" } }, 400);
    }

    private async Task HandleUnloadModelAsync(HttpListenerContext ctx)
    {
        var req = await SseStreamer.ReadJsonAsync<Dictionary<string, string>>(ctx.Request);
        if (req == null || !req.TryGetValue("id", out var modelId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "id required", Type = "invalid_request" } }, 400);
            return;
        }

        var ok = _models.TryUnloadModel(modelId);
        if (ok)
            await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Ok = true, Message = "Unloaded" });
        else
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Model not found: {modelId}", Type = "model_error" } }, 404);
    }

    private async Task HandleTokenizeAsync(HttpListenerContext ctx, string? clientId)
    {
        var req = await SseStreamer.ReadJsonAsync<TokenizeRequest>(ctx.Request);
        if (req == null || string.IsNullOrEmpty(req.Text))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
            return;
        }

        // JSON null overrides the DTO default — return 400 rather than a 500 from
        // TryGetSlot(null) (M-10).
        if (string.IsNullOrWhiteSpace(req.Model))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "model is required", Type = "invalid_request" } }, 400);
            return;
        }

        // Tokenization goes through the child server's own /tokenize endpoint for
        // process-backend models.
        if (IsProcessModel(req.Model))
        {
            var baseUrl = await EnsureProcessBaseAsync(req.Model, CancellationToken.None);
            if (baseUrl != null)
            {
                await ProxyRequestHandler.ForwardAsync(ctx, baseUrl, null, CancellationToken.None);
                return;
            }
        }

        var slot = _models.TryGetSlot(req.Model) ?? _models.TryGetSlot(_models.MainModelId);
        if (slot == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }

        // Use tokenizer via a temporary context. Model can be null for
        // embedding-only slots (and are null before LoadAllAsync completes) — guard the NRE.
        if (slot.Model == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model weights not loaded", Type = "model_error" } }, 503);
            return;
        }
        using var tempCtx = slot.Model.CreateContext(SessionRegistry.CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers));
        var tokenIds = tempCtx.Tokenize(req.Text, addBos: false).Select(t => (int)t).ToArray();
        await SseStreamer.WriteJsonAsync(ctx.Response, new TokenizeResponse
        {
            Tokens = tokenIds.Length,
            TokenIds = tokenIds
        });
    }

    // ── Helpers ──────────────────────────────────────

    /// <summary>
    /// True when modelPath resolves inside the given models_root.
    /// Always true when no models_root is configured (trusted localhost setups).
    /// Uses Path.GetFullPath comparison — immune to ../ traversal tricks.
    /// Static + internal for unit-testability; instance callers go through the wrapper below.
    /// </summary>
    internal static bool IsAllowedModelPath(string modelPath, string? modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot)) return true;

        var fullModel = Path.GetFullPath(modelPath);
        var fullRoot = Path.GetFullPath(modelsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // OrdinalIgnoreCase: match ModelSlot.ResolveModelPath, whose root-containment
        // check is case-insensitive on case-insensitive filesystems (macOS/Windows);
        // a case-differing path must not slip past this router-side gate.
        return fullModel.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullModel.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cross-client authorization helper: a client may only act on its own identity.
    /// The client id in the request path must equal the validated X-Client-Id header.
    /// </summary>
    private static bool MatchesHeaderClient(string pathClientId, string? headerClientId)
        => !string.IsNullOrEmpty(pathClientId)
           && !string.IsNullOrEmpty(headerClientId)
           && string.Equals(pathClientId, headerClientId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the KV-cache session for a request, or null for stateless inference.
    /// Callers must treat (null session + non-null sessionId) as a 404 condition.
    /// </summary>
    private SessionContext? ResolveSession(string clientId, string? sessionId)
        => sessionId != null ? _sessions.GetSession(clientId, sessionId) : null;

    private static Task WriteInvalidClientAsync(HttpListenerResponse res)
        => SseStreamer.WriteJsonAsync(res,
            new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);

    private static Task WriteSessionNotFoundAsync(HttpListenerResponse res, string sessionId)
        => SseStreamer.WriteJsonAsync(res,
            new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);

    /// <summary>
    /// Read and deserialize a JSON request body. On failure, writes a 400 response
    /// and returns ok=false so the handler can stop.
    /// </summary>
    private static async Task<(T? req, bool ok, byte[] rawBody)> ReadBodyOrErrorAsync<T>(HttpListenerContext ctx, CancellationToken ct)
    {
        // Buffer the raw body ONCE: process-backend proxying forwards these bytes
        // because HttpListener's InputStream cannot be re-read after parsing.
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        long total = 0;
        bool tooLarge = false;
        while ((read = await ctx.Request.InputStream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > SseStreamer.MaxRequestBodyBytes)
            {
                // Drain-and-discard: never leave the client blocked mid-write.
                tooLarge = true;
                continue;
            }
            ms.Write(buffer, 0, read);
        }

        var rawBody = ms.ToArray();
        T? req = default;
        if (!tooLarge)
        {
            var body = System.Text.Encoding.UTF8.GetString(rawBody);
            if (!string.IsNullOrWhiteSpace(body))
            {
                // v-fix: malformed JSON previously propagated JsonException uncaught and
                // surfaced as a generic 500 — a client error must be a 400.
                try { req = System.Text.Json.JsonSerializer.Deserialize<T>(body, SseStreamer.JsonOptions); }
                catch (System.Text.Json.JsonException) { req = default; }
            }
        }

        if (req != null)
            return (req, true, rawBody);

        await SseStreamer.WriteJsonAsync(ctx.Response,
            new ErrorResponse { Error = new() { Message = tooLarge ? "Request body too large" : "Invalid request body", Type = "invalid_request" } }, 400);
        return (default, false, rawBody);
    }

    /// <summary>
    /// Shared stateless stream selection: vision requests bypass the warm prompt cache
    /// (MTMD media queue is global per projector); otherwise warm cache if enabled.
    /// </summary>
    private IAsyncEnumerable<string> CreateStatelessStream(
        ModelSlot slot,
        string prompt,
        SamplingConfig samplingConfig,
        IReadOnlyList<byte[]>? images,
        CancellationToken ct,
        string? grammarStr = null,
        string? grammarRoot = null)
        => images is { Count: > 0 }
            ? StatelessVisionInferAsync(slot, prompt, samplingConfig, images, ct)
            : StatelessInferAsync(slot, prompt, samplingConfig, ct, _logger, grammarStr, grammarRoot);

    private static string ExtractSessionIdFromPath(string path, string suffix)
    {
        var prefix = "/eca/sessions/";
        var trimmed = path.StartsWith(prefix) ? path[prefix.Length..] : path;
        if (!string.IsNullOrEmpty(suffix) && trimmed.EndsWith(suffix))
            trimmed = trimmed[..^suffix.Length];
        return trimmed.TrimEnd('/');
    }

    private static string ExtractClientIdFromPath(string path)
    {
        // Positional split (NO empty-entry removal): '/eca/clients//heartbeat' must not
        // yield 'heartbeat' as a client id. Malformed paths (empty segments, missing id)
        // resolve to "" and are rejected downstream by the auth/validity checks.
        var parts = path.Split('/');
        if (parts.Length >= 4 &&
            parts[0] == "" && parts[1] == "eca" && parts[2] == "clients" &&
            !string.IsNullOrWhiteSpace(parts[3]))
            return parts[3];
        return "";
    }

    private static string BuildPromptFromMessages(ModelSlot slot, List<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return "";

        if (slot.Model == null)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
                sb.AppendLine($"{msg.Role}: {msg.Content}");
            return sb.ToString();
        }

        // Use the model's built-in chat template via eci_apply_chat_template.
        // Pass null template → llama.cpp selects the model's default (Qwen, Llama, ChatML, etc.)
        var chatMessages = messages
            .Select(m => (m.Role.ToLowerInvariant(), m.Content))
            .ToList();
        return slot.Model.ApplyChatTemplate(null, chatMessages, addAssistant: true);
    }

    private SamplingConfig CreateInferenceParams(ChatCompletionRequest req)
    {
        var maxTokens = req.MaxTokens;
        if (maxTokens is null && !string.IsNullOrEmpty(req.Model))
        {
            var perModel = _config.Models
                .FirstOrDefault(m => m.Id.Equals(req.Model, StringComparison.OrdinalIgnoreCase))?.MaxTokens ?? 0;
            if (perModel > 0) maxTokens = perModel;
        }
        // Stop sequences are applied by StopFilter at the stream level (the native
        // engine has no stop support) — handlers pass req.Stop to StopFilter.ApplyAsync.
        // Client grammar on plain chat is not yet wired through this factory
        // (structured/tools grammars are built server-side in the handlers).
        return CreateInferenceParams(req.Temperature, req.TopP, req.TopK, req.RepeatPenalty, maxTokens, req.Stop);
    }

    // Grammar is created per-request and passed to SampleWithGrammar.
    // This returns the sampling config; the grammar itself is created in the handler.
    private static SamplingConfig CreateGrammarInferenceParams(
        string grammar, float? temperature, float? topP, int? topK, float? repeatPenalty, int? maxTokens)
        => CreateInferenceParams(temperature, topP, topK, repeatPenalty, maxTokens, null);

    /// <summary>
    /// Effective chat output budget: explicit request value wins; otherwise the
    /// per-model catalog default (ModelConfig.MaxTokens, 0 = unset); otherwise the
    /// per-model config entry (covers process-backend models not present in _slots);
    /// otherwise the caller's fallback (global inference default).
    /// </summary>
    private int EffectiveMaxTokens(string? modelId, int? reqMaxTokens, int fallback)
    {
        if (reqMaxTokens.HasValue) return reqMaxTokens.Value;
        if (!string.IsNullOrEmpty(modelId))
        {
            // Config lookup (not _slots): covers process-backend models and cold starts
            var perModel = _config.Models
                .FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))?.MaxTokens ?? 0;
            if (perModel > 0) return perModel;
        }
        return fallback;
    }

    /// <summary>Server-side upper bound for client-supplied max_tokens. Prevents a
    /// single request from pinning the model generating far beyond any usable answer.</summary>
    internal const int MaxInferenceTokens = 32768;

    /// <summary>
    /// v15: Clamp a SESSION request's MaxTokens to the KV cache's remaining headroom
    /// (ContextSize − ApproxTokenCount − 16-token framing slack). Shift-incapable
    /// models (Qwen 2D-RoPE — MemoryCanShift=false) physically cannot truncate the
    /// KV cache in place, so any generation that runs past the wall throws
    /// ContextOverflowed and destroys the session. The clamp bounds the worst case a
    /// single turn can add; it cannot fix an already-full cache — Core's compaction
    /// handles that. Statelessness (session==null) and embedding models are skipped.
    /// Stateless helpers (decompose/planner/summary) arrive with SessionId=null after
    /// the v15 Core fix, so they never hit this path — by design.
    /// </summary>
    private SamplingConfig ClampToSessionHeadroom(SessionContext? session, SamplingConfig samplingConfig, bool structured)
    {
        if (session == null) return samplingConfig;
        try
        {
            var used = session.ApproxTokenCount;
            var headroom = (int)session.ContextSize - used - 16;
            if (headroom < 1) return samplingConfig;
            if (session.ContextSize > 0 && samplingConfig.MaxTokens > headroom)
            {
                var before = samplingConfig.MaxTokens;
                // SamplingConfig is a record with init-only properties — create a new one
                samplingConfig = samplingConfig with { MaxTokens = headroom };
                _logger.Warn("Router", $"[Headroom] session {session.SessionId} max_tokens {before} → {headroom} (ctx {session.ContextSize}, used ~{used}{(structured ? ", structured" : "")})");
            }
        }
        catch { /* headroom clamp is best-effort */ }
        return samplingConfig;
    }

    /// <summary>
    /// v14.7: Check if the accumulated output is a complete, valid JSON decision envelope.
    /// Called after each token during structured generation to enable early termination.
    /// Uses brace-matching + JSON parse — only returns true when the envelope is fully formed.
    /// </summary>
    private static bool TryParseCompleteEnvelope(string accumulated, out DecisionEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(accumulated) || accumulated.Length < 10)
            return false;

        // Quick check: must start with { and have at least one } after the thinking field.
        // The grammar always produces {"thinking": "...", ...} so we need at least 2 key-value pairs.
        var firstBrace = accumulated.IndexOf('{');
        if (firstBrace < 0) return false;

        // Count braces — the envelope is complete when braces balance (naive but effective
        // because the grammar only produces valid JSON structure, no nested braces in strings
        // except escaped ones which we can ignore for the count heuristic).
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = firstBrace; i < accumulated.Length; i++)
        {
            var ch = accumulated[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') { inString = false; }
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    // Braces balanced — try to parse the substring as a complete envelope.
                    var candidate = accumulated.Substring(firstBrace, i - firstBrace + 1);
                    try
                    {
                        // Escape any raw control chars before parsing (defense in depth —
                        // same logic as StructuredDecoder.EscapeUnescapedControlChars).
                        var sanitized = ECAssistant.LLM.Engine.StructuredDecoder.Decode(candidate);
                        envelope = sanitized;
                        return envelope != null && (envelope.HasAnswer || envelope.HasToolCalls);
                    }
                    catch { return false; }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Native OpenAI tools mode (in-process backend): grammar-forced tool_calls
    /// generation decoded into a typed message.tool_calls response.
    /// </summary>
    private async Task HandleNativeToolsChatAsync(
        HttpListenerContext ctx, ChatCompletionRequest req,
        SessionContext? session, ModelSlot templateSlot, string prompt, List<byte[]> images, CancellationToken ct)
    {
        var tools = req.Tools!.Where(t => t.IsValid).ToList();
        var fingerprint = ToolsetFingerprint.Compute(tools);
        if (session != null && session.ToolsHash != fingerprint)
        {
            if (session.ToolsHash != null)
            {
                _logger.Warn("Router", $"[Tools] session {session.SessionId} toolset changed ({session.ToolsHash[..Math.Min(8, session.ToolsHash.Length)]} → {fingerprint[..8]}) — resetting KV cache");
                try
                {
                    session.Reset();
                }
                catch (Exception ex)
                {
                    _logger.Error("Router", $"[Tools] cache reset failed on toolset change: {ex.Message}");
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = $"Toolset changed and KV cache reset failed: {ex.Message}", Type = "model_error" } }, 503);
                    return;
                }
            }
            else
            {
                _logger.Info("Router", $"[Tools] session {session.SessionId} adopting toolset {fingerprint[..8]}");
            }
            session.ToolsHash = fingerprint;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        var earlyStop = false;
        var toolsParams = CreateToolsInferenceParams(req);
        // v15: same headroom clamp for native-tools session requests.
        if (session != null) toolsParams = ClampToSessionHeadroom(session, toolsParams, structured: true);
        try
        {
        if (session != null)
        {
            await foreach (var token in session.InferAsync(prompt, toolsParams, ct, images,
                grammarStr: ToolCallGrammarFactory.Build(req.Tools!, _config.Inference.MaxParallelToolCalls), grammarRoot: ToolCallGrammarFactory.Root))
            {
                sb.Append(token);
                if (TryParseCompleteJson(sb.ToString())) { earlyStop = true; break; }
            }
        }
        else
        {
            await foreach (var token in CreateStatelessStream(templateSlot, prompt, toolsParams, images, ct,
                grammarStr: ToolCallGrammarFactory.Build(req.Tools!, _config.Inference.MaxParallelToolCalls), grammarRoot: ToolCallGrammarFactory.Root))
            {
                sb.Append(token);
                if (TryParseCompleteJson(sb.ToString())) { earlyStop = true; break; }
            }
        }
        }
        catch (Exception ex) when (IsContextOverflow(ex))
        {
            RecoverFromContextOverflow(session);
            await WriteContextOverflowResponseAsync(ctx);
            return;
        }

        sw.Stop();
        _logger.Info("Router", $"[Tools] generated {sb.Length} chars (earlyStop={earlyStop}) in {sw.ElapsedMilliseconds} ms");
        try
        {
            var calls = ToolCallDecoder.Decode(sb.ToString(), tools);
            await WriteToolCallsResponseAsync(ctx.Response, req.Model, calls);
        }
        catch (InvalidToolCallException ex)
        {
            _logger.Warn("Router", $"Tool-call decode failed: {ex.Message} | raw: {sb.ToString()[..Math.Min(sb.Length, 300)]}");
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Invalid tool_calls output: {ex.Message}", Type = "invalid_tool_calls" } }, 422);
        }
    }

    /// <summary>Writes an OpenAI chat.completion response with typed tool_calls
    /// (arguments serialized as a JSON string per the wire format), finish_reason="tool_calls".</summary>
    private static async Task WriteToolCallsResponseAsync(HttpListenerResponse response, string model, IReadOnlyList<ToolCall> calls)
    {
        var toolCalls = calls.Select(c => new
        {
            id = $"call_{Guid.NewGuid():N}",
            type = "function",
            function = new { name = c.Name, arguments = c.ArgumentsJson },
        }).ToArray();
        var responseObj = new
        {
            id = Guid.NewGuid().ToString("N"),
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = (string?)null, tool_calls = toolCalls },
                    finish_reason = "tool_calls"
                }
            }
        };
        await SseStreamer.WriteJsonAsync(response, responseObj);
    }

    /// <summary>Early-stop check: true when the accumulated output parses as a complete
    /// JSON document (the grammar does not stop LLamaSharp's token loop by itself).</summary>
    private static bool TryParseCompleteJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>True when the exception is a KV context overflow. Models without native
    /// memory shifting cannot satisfy TruncateAndReprefill in place and throw instead.</summary>
      /// <summary>Safety net: reset every live session (in-process + process-backed) after a
      /// context overflow that escaped the per-request guards. Called from the server's top-level
      /// handler so no unguarded path can leave a wedged KV cache behind.</summary>
    public void ResetAllSessionsForOverflow()
        {
            try { _sessions.ResetAllForOverflow(); } catch { }
            try { _processSessions?.ResetAllForOverflow(); } catch { }
            try { _batchSessions?.ResetAllForOverflow(); } catch { }
         }

    private static bool IsContextOverflow(Exception ex) =>
        ex.Message.Contains("Context overflowed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("native memory shifting", StringComparison.OrdinalIgnoreCase);

    /// <summary>Overflow recovery: drop the session KV so the next request re-prefills
    /// from scratch. For shift-incapable models this is the only way back to a usable
    /// session — the cache cannot be truncated in place.</summary>
    private void RecoverFromContextOverflow(SessionContext? session)
    {
        if (session == null) return;
        try
        {
            session.Reset();
            _logger.Warn("Router", $"[Overflow] session {session.SessionId} KV cache reset after context overflow — client should re-prefill and retry");
        }
        catch (Exception resetEx)
        {
            _logger.Error("Router", $"[Overflow] session reset failed: {resetEx.Message}");
        }
    }

    /// <summary>Process-backend overload: transcript-backed sessions use the same Reset contract.</summary>
    private void RecoverFromContextOverflow(ProcessSession? session)
    {
        if (session == null) return;
        try
        {
            session.Reset();
            _logger.Warn("Router", $"[Overflow] process session {session.Key} reset after context overflow — client should retry");
        }
        catch (Exception resetEx)
        {
            _logger.Error("Router", $"[Overflow] process session reset failed: {resetEx.Message}");
        }
    }

    /// <summary>Typed 413 response after overflow recovery. Best-effort: mid-stream the
    /// headers are already gone — the reset still happened, so a client retry succeeds.</summary>
    private static async Task WriteContextOverflowResponseAsync(HttpListenerContext ctx)
    {
        try
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Context overflowed — server KV cache reset; re-prefill and retry.", Type = "context_overflow" } }, 413);
        }
        catch { /* response may already be streaming */ }
    }

    // Grammar support: not yet available in ECAssistantInference. Tool-call
    // termination is handled by early-stop JSON parsing (TryParseCompleteJson).
    private static SamplingConfig CreateToolsInferenceParams(ChatCompletionRequest req)
        => new()
        {
            Temperature = req.Temperature ?? 0.3f,
            TopP = req.TopP ?? 0.95f,
            TopK = req.TopK ?? 40,
            RepeatPenalty = req.RepeatPenalty ?? 1.1f,
            MaxTokens = Math.Clamp(req.MaxTokens ?? 1024, 1, MaxInferenceTokens),
            IgnoreEos = true,  // grammar constrains output — don't let EOS end generation prematurely
        };

    // Grammar support: not yet available in ECAssistantInference. Decision envelope
    // termination is handled by early-stop JSON parsing (TryParseCompleteEnvelope).
    private static SamplingConfig CreateStructuredInferenceParams(ChatCompletionRequest req, int maxTokens)
        => new()
        {
            Temperature = req.Temperature ?? 0.3f,
            TopP = req.TopP ?? 0.95f,
            TopK = req.TopK ?? 40,
            RepeatPenalty = req.RepeatPenalty ?? 1.1f,
            MaxTokens = Math.Clamp(maxTokens, 1, MaxInferenceTokens),
            IgnoreEos = true,
        };

    private static SamplingConfig CreateInferenceParams(CompletionRequest req)
        => CreateInferenceParams(req.Temperature, req.TopP, req.TopK, req.RepeatPenalty, req.MaxTokens, req.Stop);

    private static SamplingConfig CreateInferenceParams(
        float? temperature,
        float? topP,
        int? topK,
        float? repeatPenalty,
        int? maxTokens,
        List<string>? stop)
        => new()
        {
            Temperature = temperature ?? 0.3f,
            TopP = topP ?? 0.95f,
            TopK = topK ?? 40,
            RepeatPenalty = repeatPenalty ?? 1.1f,
            MaxTokens = Math.Clamp(maxTokens ?? 512, 1, MaxInferenceTokens),
        };

    private static async IAsyncEnumerable<string> StatelessInferAsync(
        ModelSlot slot,
        string prompt,
        SamplingConfig samplingConfig,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        ILogger? logger = null,
        string? grammarStr = null,
        string? grammarRoot = null)
    {
        using var ctx = slot.Model!.CreateContext(SessionRegistry.CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers));
        using var exec = ctx.CreateExecutor();
        // Grammar failure must NOT be swallowed — an unconstrained structured request
        // silently corrupts the envelope (markdown prose instead of JSON). Let it throw:
        // the handler returns 5xx and the client falls back to the capped plain path.
        IGrammar? grammar = null;
        if (!string.IsNullOrEmpty(grammarStr) && !string.IsNullOrEmpty(grammarRoot))
        {
            logger?.Debug("Grammar", $"Stateless creating grammar: root={grammarRoot}, gbnf={grammarStr.Length} chars");
            grammar = slot.Model!.CreateGrammar(grammarStr, grammarRoot);
            logger?.Debug("Grammar", "Stateless grammar attached");
        }
        try
        {
            exec.Prompt(prompt);
            if (exec.Infer() != ECAssistantInference.Abstractions.InferResult.Ok)
            {
                logger?.Warn("Stateless", "Prefill infer failed");
                yield break;
            }
            logger?.Debug("Stateless", $"Prefill OK, sampling max {samplingConfig.MaxTokens} tokens, grammar={(grammar != null ? "yes" : "no")}");

            var maxTokens = samplingConfig.MaxTokens;
            for (int i = 0; i < maxTokens; i++)
            {
                ct.ThrowIfCancellationRequested();
                int token;
                try
                {
                    token = grammar != null
                        ? exec.SampleWithGrammar(samplingConfig, grammar)
                        : exec.Sample(samplingConfig);
                }
                catch { break; }
                if (ctx.IsEos(token)) break;
                var piece = ctx.TokenToPiece(token);
                yield return piece;
                exec.PromptTokens(new[] { token });
                if (exec.Infer() != ECAssistantInference.Abstractions.InferResult.Ok)
                    break;
            }
        }
        finally { grammar?.Dispose(); }
    }

    private static async IAsyncEnumerable<string> StatelessVisionInferAsync(
        ModelSlot slot,
        string prompt,
        SamplingConfig samplingConfig,
        IReadOnlyList<byte[]> images,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var vision = slot.Vision ?? throw new InvalidOperationException("mmproj not loaded for vision request");
        using var ctx = slot.Model!.CreateContext(SessionRegistry.CreateCtxConfig(slot.Config, slot.EffectiveGpuLayers));
        using var exec = ctx.CreateExecutor();

        var marker = MtmdMarkerResolver.GetMarker(vision);
        prompt = prompt.Replace(ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, marker);

        exec.PromptWithImages(prompt, slot.Model!, images.ToArray());
        if (exec.Infer() != ECAssistantInference.Abstractions.InferResult.Ok)
            yield break;

        var maxTokens = samplingConfig.MaxTokens;
        for (int i = 0; i < maxTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            int token;
            try { token = exec.Sample(samplingConfig); }
            catch { break; }
            if (ctx.IsEos(token)) break;
            var piece = ctx.TokenToPiece(token);
            yield return piece;
            exec.PromptTokens(new[] { token });
            if (exec.Infer() != ECAssistantInference.Abstractions.InferResult.Ok)
                break;
        }
    }

    // ── Batch-aware session management wrappers ─────────────────
    // These methods check if a session lives in the BatchSessionRegistry first.
    // If found there, they operate on the batch session. Otherwise, they fall
    // through to the standard SessionRegistry path. When continuous_batching is
    // off (_batchSessions == null), they always use the standard path.

    private async Task HandleCreateBatchSessionAsync(HttpListenerContext ctx, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing or unregistered X-Client-Id header", Type = "invalid_client" } }, 401);
            return;
        }

        var req = await SseStreamer.ReadJsonAsync<CreateSessionRequest>(ctx.Request, ct);
        if (req == null || string.IsNullOrEmpty(req.SessionId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "session_id required", Type = "invalid_request" } }, 400);
            return;
        }

        try
        {
            var requestedModelId = string.IsNullOrWhiteSpace(req.ModelId)
                ? _models.MainModelId
                : req.ModelId;

            // Unknown model id: fall back to main model (same leniency as standard path)
            if (_models.TryGetSlot(requestedModelId) == null)
            {
                _logger.Warn("Router", $"CreateBatchSession: model '{requestedModelId}' not configured — falling back to main model '{_models.MainModelId}'");
                requestedModelId = _models.MainModelId;
            }

            var cid = clientId ?? throw new InvalidOperationException("clientId is null");
            var batchReg = _batchSessions ?? throw new InvalidOperationException("Batch sessions not enabled");
            var session = batchReg.CreateSession(cid, req.SessionId, requestedModelId, req.ToolsHash);
            _logger.Info("Router", $"Created batch session '{req.SessionId}' on model '{requestedModelId}'");

            await SseStreamer.WriteJsonAsync(ctx.Response, new
            {
                session_id = req.SessionId,
                client_id = clientId,
                model_id = requestedModelId,
                tools_hash = req.ToolsHash,
                context_size = session.ContextSize,
                estimated_vram_mb = session.EstimatedVramMb
            });
        }
        catch (Exception ex)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = ex.Message, Type = "session_error" } }, 400);
        }
    }

    private async Task HandlePrefillOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId, CancellationToken ct)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                var req = await SseStreamer.ReadJsonAsync<PrefillRequest>(ctx.Request, ct);
                if (req == null || string.IsNullOrEmpty(req.Text))
                {
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
                    return;
                }
                var (success, tokens, elapsedMs) = await batchSession.PrefillAsync(req.Text, ct);
                await SseStreamer.WriteJsonAsync(ctx.Response, new { ok = true, message = $"Prefilled {tokens} tokens in {elapsedMs}ms", tokens = tokens, elapsed_ms = elapsedMs });
                return;
            }
        }
        await HandlePrefillAsync(ctx, clientId, sessionId, ct);
    }

    private async Task HandleRewindOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                var success = await batchSession.RewindAsync();
                await SseStreamer.WriteJsonAsync(ctx.Response, new { ok = success, message = success ? "Rewound to saved state" : "Rewind failed (no saved state)" });
                return;
            }
        }
        await HandleRewindAsync(ctx, clientId, sessionId);
    }

    private async Task HandleSaveStateOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                var success = await batchSession.SaveStateAsync();
                await SseStreamer.WriteJsonAsync(ctx.Response, new { ok = success, message = success ? "State saved" : "Save failed" });
                return;
            }
        }
        await HandleSaveStateAsync(ctx, clientId, sessionId);
    }

    private async Task HandleResetOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                try { await batchSession.ResetAsync(); await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Message = "Batch session reset" }); }
                catch (Exception ex) { await SseStreamer.WriteJsonAsync(ctx.Response, new ErrorResponse { Error = new() { Message = ex.Message, Type = "session_error" } }, 500); }
                return;
            }
        }
        await HandleResetAsync(ctx, clientId, sessionId);
    }

    private async Task HandleEvaluateOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId, CancellationToken ct)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                var req = await SseStreamer.ReadJsonAsync<EvaluateRequest>(ctx.Request, ct);
                if (req == null || string.IsNullOrEmpty(req.Text))
                {
                    await SseStreamer.WriteJsonAsync(ctx.Response,
                        new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
                    return;
                }
                var maxTokens = req.MaxTokens ?? 1;
                var (success, sampled, approx) = await batchSession.EvaluateAsync(req.Text, maxTokens, ct);
                await SseStreamer.WriteJsonAsync(ctx.Response, new { ok = success, message = $"Evaluated {sampled} tokens", sampled_tokens = sampled, approx_tokens = approx });
                return;
            }
        }
        await HandleEvaluateAsync(ctx, clientId, sessionId, ct);
    }

    private async Task HandleSessionStatusOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response, new
                {
                    session_id = batchSession.SessionId,
                    client_id = batchSession.ClientId,
                    model_id = batchSession.ModelId,
                    is_prefilled = batchSession.IsPrefilled,
                    has_saved_state = batchSession.HasSavedState,
                    approx_tokens = batchSession.ApproxTokenCount,
                    context_size = batchSession.ContextSize,
                    estimated_vram_mb = batchSession.EstimatedVramMb,
                    created_at = batchSession.CreatedAt.ToString("o"),
                    last_activity = batchSession.LastActivity.ToString("o")
                });
                return;
            }
        }
        await HandleSessionStatusAsync(ctx, clientId, sessionId);
    }

    private async Task HandleDestroySessionOrBatchAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (_batchSessions != null)
        {
            var batchSession = _batchSessions.GetSession(clientId ?? "", sessionId);
            if (batchSession != null)
            {
                var success = _batchSessions.DestroySession(clientId ?? "", sessionId);
                await SseStreamer.WriteJsonAsync(ctx.Response, new { ok = success, message = success ? $"Batch session '{sessionId}' destroyed" : $"Failed to destroy batch session '{sessionId}'" });
                return;
            }
        }
        await HandleDestroySessionAsync(ctx, clientId, sessionId);
    }

}