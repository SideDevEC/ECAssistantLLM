using System.Net;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
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

    public RequestRouter(
        MultiModelHost models,
        SessionRegistry sessions,
        IInferenceScheduler scheduler,
        VramBudget vram,
        IClientManager clients,
        LlmServerConfig config,
        ILogger logger,
        CancellationTokenSource cts)
    {
        _models = models;
        _sessions = sessions;
        _scheduler = scheduler;
        _vram = vram;
        _clients = clients;
        _config = config;
        _logger = logger;
        _cts = cts;
    }

    public async Task RouteAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        _logger.Debug("Router", $"{method} {path}");

        var clientId = req.Headers["X-Client-Id"] ?? "default";

        // ── OpenAI-compatible endpoints ──
        if (path == "/v1/chat/completions" && method == "POST")
        { await HandleChatCompletionAsync(ctx, clientId, ct); return; }

        if (path == "/v1/completions" && method == "POST")
        { await HandleCompletionAsync(ctx, clientId, ct); return; }

        if (path == "/v1/embeddings" && method == "POST")
        { await HandleEmbeddingsAsync(ctx, clientId, ct); return; }

        if (path == "/v1/models" && method == "GET")
        { await HandleListModelsAsync(ctx); return; }

        // ── ECAssistant extension endpoints ──

        // Health
        if (path == "/eca/health" && method == "GET")
        { await HandleHealthAsync(ctx); return; }

        // Client management
        if (path == "/eca/clients" && method == "POST")
        { await HandleRegisterClientAsync(ctx); return; }

        if (path.StartsWith("/eca/clients/") && path.EndsWith("/heartbeat") && method == "POST")
        { await HandleHeartbeatAsync(ctx, ExtractClientIdFromPath(path)); return; }

        if (path.StartsWith("/eca/clients/") && method == "DELETE")
        { await HandleDisconnectClientAsync(ctx, ExtractClientIdFromPath(path)); return; }

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

    // ── OpenAI handlers ──────────────────────────────────

    private async Task HandleChatCompletionAsync(HttpListenerContext ctx, string? clientId, CancellationToken ct)
    {
        var req = await SseStreamer.ReadJsonAsync<ChatCompletionRequest>(ctx.Request, ct);
        if (req == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Invalid request body", Type = "invalid_request" } }, 400);
            return;
        }

        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = req.SessionId != null
            ? _sessions.GetSession(clientId, req.SessionId)
            : null;

        if (session == null && req.SessionId != null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {req.SessionId}", Type = "session_not_found" } }, 404);
            return;
        }

        var prompt = BuildPromptFromMessages(req.Messages);
        var inferenceParams = CreateInferenceParams(req);

        await using var slot = await _scheduler.AcquireAsync(ct);

        if (req.Stream)
        {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = session.InferAsync(prompt, inferenceParams, ct);
            }
            else
            {
                var modelSlot = _models.TryGetSlot(req.Model) ?? _models.GetMainSlot();
                tokenStream = StatelessInferAsync(modelSlot, prompt, inferenceParams, ct);
            }

            await SseStreamer.StreamAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();
            if (session != null)
            {
                await foreach (var token in session.InferAsync(prompt, inferenceParams, ct))
                    sb.Append(token);
            }
            else
            {
                var modelSlot = _models.TryGetSlot(req.Model) ?? _models.GetMainSlot();
                await foreach (var token in StatelessInferAsync(modelSlot, prompt, inferenceParams, ct))
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

    private async Task HandleCompletionAsync(HttpListenerContext ctx, string? clientId, CancellationToken ct)
    {
        var req = await SseStreamer.ReadJsonAsync<CompletionRequest>(ctx.Request, ct);
        if (req == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Invalid request body", Type = "invalid_request" } }, 400);
            return;
        }

        if (string.IsNullOrEmpty(req.Prompt))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "prompt is required", Type = "invalid_request" } }, 400);
            return;
        }

        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        // Optional session-based inference (KV cache)
        var session = req.SessionId != null
            ? _sessions.GetSession(clientId, req.SessionId)
            : null;

        if (session == null && req.SessionId != null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {req.SessionId}", Type = "session_not_found" } }, 404);
            return;
        }

        var prompt = req.Prompt;
        // Echo: if true, the prompt text is prepended to the response (handled below)

        var inferenceParams = CreateInferenceParams(req);

        await using var slot = await _scheduler.AcquireAsync(ct);

        if (req.Stream)
        {
            IAsyncEnumerable<string> tokenStream;
            if (session != null)
            {
                tokenStream = session.InferAsync(prompt, inferenceParams, ct);
            }
            else
            {
                var modelSlot = _models.TryGetSlot(req.Model) ?? _models.GetMainSlot();
                tokenStream = StatelessInferAsync(modelSlot, prompt, inferenceParams, ct);
            }

            await SseStreamer.StreamCompletionAsync(ctx.Response, tokenStream, req.Model, ct);
        }
        else
        {
            var sb = new StringBuilder();
            if (req.Echo)
                sb.Append(prompt);

            if (session != null)
            {
                await foreach (var token in session.InferAsync(prompt, inferenceParams, ct))
                    sb.Append(token);
            }
            else
            {
                var modelSlot = _models.TryGetSlot(req.Model) ?? _models.GetMainSlot();
                await foreach (var token in StatelessInferAsync(modelSlot, prompt, inferenceParams, ct))
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

        var slot = _models.TryGetSlot(req.Model) ?? _models.GetEmbeddingSlot();
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
        var response = new
        {
            @object = "list",
            data = models.Select(m => new
            {
                id = m.Id,
                @object = "model",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                owned_by = "ecassistant",
                loaded = m.IsLoaded,
                is_embedding = m.IsEmbedding
            }).ToList()
        };
        await SseStreamer.WriteJsonAsync(ctx.Response, response);
    }

    // ── ECAssistant handlers ─────────────────────────────

    private async Task HandleHealthAsync(HttpListenerContext ctx)
    {
        var response = new
        {
            status = "ok",
            version = "1.0.0",
            models_loaded = _models.LoadedModelIds,
            sessions = _sessions.Count,
            clients = _clients.ClientCount,
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
            ServerVersion = "1.0.0"
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
        var alive = _sessions.ListSessions().Count(s => s.ClientId == clientId);
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

        // Respond OK before shutting down
        await SseStreamer.WriteJsonAsync(ctx.Response,
            new SuccessResponse { Message = "Shutting down" });

        // If no clients remain, trigger shutdown
        if (_clients.ClientCount == 0)
        {
            _logger.Info("Router", "Last client shutdown — winding down server...");
            // Small delay to let the response flush
            await Task.Delay(100);
            _cts.Cancel();
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);
            return;
        }

        var req = await SseStreamer.ReadJsonAsync<PrefillRequest>(ctx.Request, ct);
        if (req == null || string.IsNullOrEmpty(req.Text))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "text required", Type = "invalid_request" } }, 400);
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);
            return;
        }

        session.Reset();
        await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Message = "KV cache reset" });
    }

    private async Task HandleSessionStatusAsync(HttpListenerContext ctx, string? clientId, string sessionId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var session = _sessions.GetSession(clientId, sessionId);
        if (session == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = $"Session not found: {sessionId}", Type = "session_not_found" } }, 404);
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
                new ErrorResponse { Error = new() { Message = "Missing X-Client-Id header", Type = "invalid_request" } }, 400);
            return;
        }

        var ok = _sessions.DestroySession(clientId, sessionId);
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

        var modelConfig = new Config.ModelConfig
        {
            Id = req.Id,
            Path = req.Path,
            GpuLayers = req.GpuLayers,
            ContextSize = req.ContextSize,
            Threads = req.Threads,
            IsEmbedding = req.IsEmbedding
        };

        var ok = _models.TryLoadModel(modelConfig);
        await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Ok = ok, Message = ok ? "Loaded" : "Failed to load" });
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
        await SseStreamer.WriteJsonAsync(ctx.Response, new SuccessResponse { Ok = ok, Message = ok ? "Unloaded" : "Not found" });
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

        var slot = _models.TryGetSlot(req.Model) ?? _models.GetMainSlot();
        if (slot.Weights == null)
        {
            await SseStreamer.WriteJsonAsync(ctx.Response,
                new ErrorResponse { Error = new() { Message = "Model not loaded", Type = "model_error" } }, 503);
            return;
        }

        // Use LLamaSharp tokenizer via a temporary context
        using var tempCtx = slot.Weights.CreateContext(slot.Params);
        var tokenIds = tempCtx.Tokenize(req.Text, addBos: false).Select(t => (int)t).ToArray();
        await SseStreamer.WriteJsonAsync(ctx.Response, new TokenizeResponse
        {
            Tokens = tokenIds.Length,
            TokenIds = tokenIds
        });
    }

    // ── Helpers ──────────────────────────────────────────

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
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "eca" && parts[1] == "clients")
            return parts[2];
        return "";
    }

    private static string BuildPromptFromMessages(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.AppendLine($"{msg.Role}: {msg.Content}");
        }
        return sb.ToString();
    }

    private static LLama.Common.InferenceParams CreateInferenceParams(ChatCompletionRequest req)
    {
        // DefaultSamplingPipeline properties are init-only — use object initializer
        var pipe = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = req.Temperature ?? 0.3f,
            TopP = req.TopP ?? 0.95f,
            TopK = req.TopK ?? 40,
            RepeatPenalty = req.RepeatPenalty ?? 1.1f
        };

        return new LLama.Common.InferenceParams
        {
            MaxTokens = req.MaxTokens ?? 512,
            AntiPrompts = req.Stop?.ToArray() ?? new[] { "</s>", "User:", "### User" },
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = pipe
        };
    }

    private static LLama.Common.InferenceParams CreateInferenceParams(CompletionRequest req)
    {
        var pipe = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = req.Temperature ?? 0.3f,
            TopP = req.TopP ?? 0.95f,
            TopK = req.TopK ?? 40,
            RepeatPenalty = req.RepeatPenalty ?? 1.1f
        };

        return new LLama.Common.InferenceParams
        {
            MaxTokens = req.MaxTokens ?? 512,
            AntiPrompts = req.Stop?.ToArray() ?? new[] { "</s>", "User:", "### User" },
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = pipe
        };
    }

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
}