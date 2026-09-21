using System.Net;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Config;
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
        ProcessSessionRegistry? processSessions = null)
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

        if (path.StartsWith("/eca/clients/") && path.EndsWith("/heartbeat") && method == "POST")
        {
            var pathClientId = ExtractClientIdFromPath(path);
            if (!MatchesHeaderClient(pathClientId, clientId))
            {
                await SseStreamer.WriteJsonAsync(res,
                    new ErrorResponse { Error = new() { Message = "X-Client-Id header does not match the client in the request path", Type = "forbidden" } }, 403);
                return;
            }
            await HandleHeartbeatAsync(ctx, pathClientId);
            return;
        }

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
        { await HandleCreateSessionAsync(ctx, clientId, ct); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/prefill") && method == "POST")
        { await HandlePrefillAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/prefill"), ct); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/rewind") && method == "POST")
        { await HandleRewindAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/rewind")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/save-state") && method == "POST")
        { await HandleSaveStateAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/save-state")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/reset") && method == "POST")
        { await HandleResetAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/reset")); return; }

        if (path.StartsWith("/eca/sessions/") && path.EndsWith("/status") && method == "GET")
        { await HandleSessionStatusAsync(ctx, clientId, ExtractSessionIdFromPath(path, "/status")); return; }

        if (path.StartsWith("/eca/sessions/") && method == "DELETE")
        { await HandleDestroySessionAsync(ctx, clientId, ExtractSessionIdFromPath(path, "")); return; }

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
        if (req.SessionId == null && !req.Structured)
        {
            if (_processStateless == null)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
                return;
            }

            if (req.Messages.Count == 0)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "messages is required", Type = "invalid_request" } }, 400);
                return;
            }

            var maxTokens = Math.Clamp(EffectiveMaxTokens(req.Model, req.MaxTokens, _config.Inference.MaxTokens), 1, MaxInferenceTokens);
            var statelessClient = _processStateless;
            if (statelessClient == null)
            {
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = "Process backend unavailable", Type = "model_error" } }, 503);
                return;
            }
            if (req.Stream)
            {
                var tokenStream = ThinkFilter.ApplyAsync(
                    statelessClient.InferStatelessAsync(req.Model, req.Messages, req, grammar: null, maxTokens, ct), ct);
                await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
            }
            else
            {
                var sb = new StringBuilder();
                await foreach (var token in ThinkFilter.ApplyAsync(
                    statelessClient.InferStatelessAsync(req.Model, req.Messages, req, grammar: null, maxTokens, ct), ct))
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
            if (req.SessionId != null)
            {
                var structuredSession = _processSessions?.Get(clientId, req.SessionId);
                if (structuredSession == null)
                {
                    await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
                    return;
                }
                structuredStream = structuredSession.InferAsync(req.Messages, req, ct, grammar: DecisionGrammar.Gbnf);
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
                var structuredMaxTokens = Math.Clamp(Math.Min(req.MaxTokens ?? 256, 256), 1, 256);
                structuredStream = stateless.InferStatelessAsync(
                    req.Model, req.Messages, req, grammar: DecisionGrammar.Gbnf, structuredMaxTokens, ct);
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
                _logger.Warn("Router", $"[Structured/process] decode failed: {ex.Message} | raw: {structuredSb.ToString()[..Math.Min(structuredSb.Length, 300)]}");
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = $"Invalid decision envelope: {ex.Message}", Type = "invalid_decision" } }, 422);
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

        if (req.Stream)
        {
            var tokenStream = ThinkFilter.ApplyAsync(session.InferAsync(req.Messages, req, ct), ct);
            await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();
            await foreach (var token in ThinkFilter.ApplyAsync(session.InferAsync(req.Messages, req, ct), ct))
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

        var session = ResolveSession(clientId, req.SessionId);
        if (session == null && req.SessionId != null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, req.SessionId);
            return;
        }

        var templateSlot = _models.TryGetSlot(req.Model) ?? _models.TryGetSlot(_models.MainModelId);
        if (templateSlot?.Weights == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }
        var prompt = BuildPromptFromMessages(templateSlot, req.Messages);
        var inferenceParams = CreateInferenceParams(req);

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

        // v13 structured mode: grammar-forced decision envelope, parsed server-side.
        // Always non-streamed — the client gets one JSON document with the decision.
        if (req.Structured)
        {
            var structuredSw = System.Diagnostics.Stopwatch.StartNew();
            _logger.Info("Router", $"[Structured] generation start (session={req.SessionId ?? "stateless"}, max_tokens={req.MaxTokens})");
            var structuredParams = CreateStructuredInferenceParams(req);
            var structuredSb = new StringBuilder();
            var earlyStop = false;
            if (session != null)
            {
                await foreach (var token in session.InferAsync(prompt, structuredParams, ct, images))
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
                await foreach (var token in CreateStatelessStream(templateSlot, prompt, structuredParams, images, ct))
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
                _logger.Warn("Router", $"Structured decode failed: {ex.Message} | raw: {structuredSb.ToString()[..Math.Min(structuredSb.Length, 300)]}");
                await SseStreamer.WriteJsonAsync(ctx.Response,
                    new ErrorResponse { Error = new() { Message = $"Invalid decision envelope: {ex.Message}", Type = "invalid_decision" } }, 422);
            }
            return;
        }

        if (req.Stream)
        {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = ThinkFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, images), ct);
            }
            else
            {
                tokenStream = ThinkFilter.ApplyAsync(CreateStatelessStream(templateSlot, prompt, inferenceParams, images, ct), ct);
            }

            await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();
            if (session != null)
            {
                await foreach (var token in ThinkFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct, images), ct))
                    sb.Append(token);
            }
            else
            {
                await foreach (var token in ThinkFilter.ApplyAsync(CreateStatelessStream(templateSlot, prompt, inferenceParams, images, ct), ct))
                    sb.Append(token);
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
        if (slot?.Weights == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }
        var prompt = req.Prompt;

        var inferenceParams = CreateInferenceParams(req);

        await using var gate = await _scheduler.AcquireAsync(ct);

        if (req.Stream)
        {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = ThinkFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct), ct);
            }
            else
            {
                tokenStream = ThinkFilter.ApplyAsync(CreateStatelessStream(slot, prompt, inferenceParams, images: null, ct), ct);
            }

            await SseStreamer.StreamCompletionAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();

            if (session != null)
            {
                await foreach (var token in ThinkFilter.ApplyAsync(session.InferAsync(prompt, inferenceParams, ct), ct))
                    sb.Append(token);
            }
            else
            {
                await foreach (var token in ThinkFilter.ApplyAsync(CreateStatelessStream(slot, prompt, inferenceParams, images: null, ct), ct))
                    sb.Append(token);
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
        if (slot == null || !slot.IsEmbedding || slot.Embedder == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Embedding model not available", Type = "model_error" } }, 503);
            return;
        }

        try
        {
            var embeddings = await slot.Embedder.GetEmbeddings(req.Input);
            var response = new EmbeddingResponse
            {
                Model = slot.Id,
                Data = embeddings.Select((emb, i) => new EmbeddingData
                {
                    Embedding = emb,
                    Index = i
                }).ToList()
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
            capabilities = new[] { "eca-extensions", "structured-decoding" },
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

    private async Task HandleHeartbeatAsync(HttpListenerContext ctx, string clientId)
    {
        var req = await SseStreamer.ReadJsonAsync<HeartbeatRequest>(ctx.Request);
        if (!_clients.IsValid(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Unknown client", Type = "client_not_found" } }, 404);
            return;
        }

        _clients.Heartbeat(clientId, req?.ActiveSessions ?? 0);
        var alive = _sessions.ListSessions().Count(s => s.ClientId == clientId)
                    + (_processSessions?.CountForClient(clientId) ?? 0);
        await SseStreamer.WriteJsonAsync(ctx.Response, new HeartbeatResponse
        {
            Ok = true,
            SessionsAlive = alive
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
            var session = _sessions.CreateSession(clientId, req.SessionId, req.ModelId);

            await SseStreamer.WriteJsonAsync(ctx.Response, new
            {
                session_id = session.SessionId,
                client_id = session.ClientId,
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
            var (psuccess, ptokens, pelapsedMs) = await procSession.PrefillAsync(req.Text, ct);
            await SseStreamer.WriteJsonAsync(ctx.Response, new PrefillResponse
            {
                Prefilled = psuccess,
                Tokens = ptokens,
                ElapsedMs = pelapsedMs
            });
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await WriteSessionNotFoundAsync(ctx.Response, sessionId);
            return;
        }

        var (success, tokens, elapsedMs) = await session.PrefillAsync(req.Text, ct);
        await SseStreamer.WriteJsonAsync(ctx.Response, new PrefillResponse
        {
            Prefilled = success,
            Tokens = tokens,
            ElapsedMs = elapsedMs
        });
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
                approx_tokens = procSession.ApproxTokenCount,
                context_size = procSession.ContextSize,
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
            approx_tokens = session.ApproxTokenCount,
            context_size = session.ContextSize,
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

        // Use LLamaSharp tokenizer via a temporary context. Weights can be null for
        // embedding-only slots (and are null before LoadAllAsync completes) — guard the NRE.
        if (slot.Weights == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model weights not loaded", Type = "model_error" } }, 503);
            return;
        }
        using var tempCtx = slot.Weights.CreateContext(slot.Params);
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
                req = System.Text.Json.JsonSerializer.Deserialize<T>(body, SseStreamer.JsonOptions);
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
        LLama.Common.InferenceParams inferenceParams,
        IReadOnlyList<byte[]>? images,
        CancellationToken ct)
        => images is { Count: > 0 }
            ? StatelessVisionInferAsync(slot, prompt, inferenceParams, images, ct)
            : StatelessInferAsync(slot, prompt, inferenceParams, ct);

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
        // Fall back to a plain transcript when the model weights aren't loaded.
        if (slot.Weights == null)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
                sb.AppendLine($"{msg.Role}: {msg.Content}");
            return sb.ToString();
        }

        // Apply the model's embedded chat template (e.g. Qwen <|im_start|>) instead of a
        // raw "role: content" transcript — raw text makes instruct models hallucinate turns.
        var template = new LLama.LLamaTemplate(slot.Weights.NativeHandle) { AddAssistant = true };
        foreach (var msg in messages)
            template.Add(msg.Role.ToLowerInvariant(), msg.Content);
        return LLama.Transformers.PromptTemplateTransformer.ToModelPrompt(template);
    }

    private LLama.Common.InferenceParams CreateInferenceParams(ChatCompletionRequest req)
    {
        // Per-model output budget: req wins, then catalog-configured model default
        // (thinking models need more room than the global default).
        var maxTokens = req.MaxTokens;
        if (maxTokens is null && !string.IsNullOrEmpty(req.Model))
        {
            // Config lookup covers process-backend models and cold starts (TryGetSlot may be null)
            var perModel = _config.Models
                .FirstOrDefault(m => m.Id.Equals(req.Model, StringComparison.OrdinalIgnoreCase))?.MaxTokens ?? 0;
            if (perModel > 0) maxTokens = perModel;
        }
        return CreateInferenceParams(req.Temperature, req.TopP, req.TopK, req.RepeatPenalty, maxTokens, req.Stop);
    }

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

    /// <summary>v13: inference params with the decision grammar injected at the sampler —
    /// the model physically cannot emit anything but a valid decision envelope.</summary>
    private static LLama.Common.InferenceParams CreateStructuredInferenceParams(ChatCompletionRequest req)
    {
        var pipe = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = req.Temperature ?? 0.3f,
            TopP = req.TopP ?? 0.95f,
            TopK = req.TopK ?? 40,
            RepeatPenalty = req.RepeatPenalty ?? 1.1f,
            Grammar = new LLama.Sampling.Grammar(DecisionGrammar.Gbnf, DecisionGrammar.Root),
        };
        // v13: NO anti-prompts here — the grammar already bounds output, and a stop
        // sequence (e.g. "User:") can legally occur inside a JSON string value,
        // truncating the envelope mid-document.
        // v14.7: Reduced default max_tokens from 512 to 256 — the envelope rarely
        // exceeds 100 tokens. Early termination in the streaming loop handles the
        // actual stop; this is just a safety cap.
        return new LLama.Common.InferenceParams
        {
            MaxTokens = Math.Clamp(req.MaxTokens ?? 256, 1, MaxInferenceTokens),
            SamplingPipeline = pipe,
        };
    }

    private static LLama.Common.InferenceParams CreateInferenceParams(CompletionRequest req)
        => CreateInferenceParams(req.Temperature, req.TopP, req.TopK, req.RepeatPenalty, req.MaxTokens, req.Stop);

    private static LLama.Common.InferenceParams CreateInferenceParams(
        float? temperature,
        float? topP,
        int? topK,
        float? repeatPenalty,
        int? maxTokens,
        List<string>? stop)
    {
        // DefaultSamplingPipeline properties are init-only — use object initializer
        var pipe = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = temperature ?? 0.3f,
            TopP = topP ?? 0.95f,
            TopK = topK ?? 40,
            RepeatPenalty = repeatPenalty ?? 1.1f
        };

        return new LLama.Common.InferenceParams
        {
            // Server-side clamp: rejects runaway/zero/negative client values.
            MaxTokens = Math.Clamp(maxTokens ?? 512, 1, MaxInferenceTokens),
            AntiPrompts = stop?.ToArray() ?? new[] { "</s>", "User:", "### User" },
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = pipe
        };
    }

    /// <summary>Cold stateless inference — fresh executor per call (pre-cache behavior).</summary>
    private static async IAsyncEnumerable<string> StatelessInferAsync(
        ModelSlot slot,
        string prompt,
        LLama.Common.InferenceParams inferenceParams,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var executor = new LLama.StatelessExecutor(
            slot.Weights!, slot.Params,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LLama.StatelessExecutor>.Instance);
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            yield return token;
    }

    /// <summary>
    /// Stateless inference with vision: fresh context + MTMD executor per request.
    /// Media is queued into the projector before the prompt runs; cleared after.
    /// </summary>
    private static async IAsyncEnumerable<string> StatelessVisionInferAsync(
        ModelSlot slot,
        string prompt,
        LLama.Common.InferenceParams inferenceParams,
        IReadOnlyList<byte[]> images,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var mtmd = slot.Mmproj ?? throw new InvalidOperationException("mmproj not loaded for vision request");
        using var context = slot.Weights!.CreateContext(slot.Params);
        // mtmd is non-null here (guaranteed by the throw above) — the plain-text
        // executor branch below was dead code and has been removed.
        var executor = new LLama.InteractiveExecutor(context, mtmd, Microsoft.Extensions.Logging.Abstractions.NullLogger<LLama.LLamaContext>.Instance);

        try
        {
            mtmd.ClearMedia();
            foreach (var img in images)
                mtmd.LoadMedia(img);

            prompt = prompt.Replace(ECAssistant.LLM.Models.ChatMessageContentConverter.DefaultImageMarker, ECAssistant.LLM.Engine.MtmdMarkerResolver.GetMarkerFor(executor));
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
                yield return token;
        }
        finally
        {
            try { mtmd.ClearMedia(); } catch { }
        }
    }

}