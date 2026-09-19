using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// One client session on a Process-backend model (child llama-server).
/// Transcript-backed: keeps the prefill prefix + conversation history server-side and
/// re-sends the full history each turn — the child's per-slot KV/prefix cache skips
/// tokens it already processed, so long chats stay efficient. The child's cache is an
/// optimization only; correctness never depends on it.
/// Mirrors the client-facing contract of <see cref="SessionContext"/>.
/// </summary>
public sealed class ProcessSession : IDisposable
{
    private readonly IProcessModelHost _host;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ModelConfig _model;

    /// <summary>Prefill prefix (system-level static text). Empty when not prefilled.</summary>
    private string _prefix = string.Empty;

    /// <summary>Conversation turns appended per request (user + assistant messages).</summary>
    private readonly List<ChatMessage> _history = new();

    /// <summary>History snapshot taken by SaveState; RewindAsync truncates back to it.</summary>
    private int _savedHistoryCount = -1;

    /// <summary>Serializes transcript mutations (SaveState/Rewind/Reset) against streaming inference.</summary>
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private bool _disposed;

    /// <summary>Composite key: {clientId}:{sessionId}.</summary>
    public string Key { get; }

    public string ClientId { get; }

    public string SessionId { get; }

    public string ModelId { get; }

    /// <summary>Context size of the underlying model (for status reporting).</summary>
    public uint ContextSize { get; }

    /// <summary>KV cache memory lives in the child process — this server reserves none.</summary>
    public double EstimatedVramMb => 0;

    /// <summary>Whether the static prefix has been prefilled.</summary>
    public bool IsPrefilled { get; private set; }

    /// <summary>Approximate token count over prefix + history (~4 chars/token).</summary>
    public int ApproxTokenCount => EstimateTokenCount(_prefix) + _history.Sum(m => EstimateTokenCount(m.Content));

    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    public ProcessSession(
        string clientId,
        string sessionId,
        ModelConfig model,
        IProcessModelHost host,
        HttpClient httpClient,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id required", nameof(clientId));
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Session id required", nameof(sessionId));

        ClientId = clientId;
        SessionId = sessionId;
        ModelId = model.Id;
        Key = $"{clientId}:{sessionId}";
        ContextSize = (uint)Math.Max(1, model.ContextSize);
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sets the static prefix and warms the child's prefix cache with a 1-token probe.
    /// Idempotent: re-prefill is a no-op, like <see cref="SessionContext"/>.
    /// </summary>
    public async Task<(bool success, int tokens, long elapsedMs)> PrefillAsync(string text, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(text))
            return (true, 0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsPrefilled) return (true, ApproxTokenCount, 0);

            _prefix = text;

            // Warm the child's KV/prefix cache so the first real turn skips re-prefill.
            // A dummy user turn is required: instruct chat templates (e.g. Qwen) raise
            // "No user query found" when only a system message is present. The prefix
            // itself still lands in the child's KV cache — that's what we're warming.
            var payload = BuildChatPayload(new[] { new ChatMessage { Role = "user", Content = "ok" } }, maxTokens: 1);
            try
            {
                using var response = await PostChatAsync(payload, stream: false, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.Warn("ProcessSession", $"[{Key}] Prefill warm-up failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 200)]}");
                    return (false, 0, sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("ProcessSession", $"[{Key}] Prefill warm-up error: {ex.Message}");
                return (false, 0, sw.ElapsedMilliseconds);
            }

            IsPrefilled = true;
            LastActivity = DateTime.UtcNow;
            _logger.Info("ProcessSession", $"[{Key}] Prefilled {ApproxTokenCount} tokens in {sw.ElapsedMilliseconds}ms");
            return (true, ApproxTokenCount, sw.ElapsedMilliseconds);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Appends the incoming messages to the transcript, sends prefix + history to the
    /// child, and yields the assistant reply as a stream of content deltas.
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage> appended = new();
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        List<ChatMessage> snapshot;
        try
        {
            snapshot = _history.ToList();
            _history.AddRange(messages);
            appended.AddRange(messages);
        }
        finally
        {
            _ioLock.Release();
        }

        LastActivity = DateTime.UtcNow;

        var payload = BuildChatPayload(messages, ClampMaxTokens(request.MaxTokens));

        var deltas = StreamDeltasAsync(payload, ct);
        var replySb = new StringBuilder();

        // Manual enumeration: yield statements cannot live inside a try block that has a
        // catch clause — MoveNextAsync runs inside the try, the yield stays outside.
        var enumerator = deltas.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                string? delta;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    delta = enumerator.Current;
                }
                catch
                {
                    // The turn failed — roll back the appended messages so the transcript
                    // stays consistent for the next attempt.
                    await _ioLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try { RemoveAppended(appended, snapshot.Count); }
                    finally { _ioLock.Release(); }
                    throw;
                }

                replySb.Append(delta);
                yield return delta;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var assistantReply = replySb.ToString();

        // Commit the assistant turn to the transcript. The appended user messages stay
        // (they are part of the conversation); only a FAILED turn rolls them back.
        await _ioLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(assistantReply))
                _history.Add(new ChatMessage { Role = "assistant", Content = assistantReply });
            if (!string.IsNullOrEmpty(_prefix)) IsPrefilled = true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Snapshots the transcript for a later rewind.</summary>
    public bool SaveState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ioLock.Wait(TimeSpan.FromSeconds(30)))
        {
            _logger.Warn("ProcessSession", $"[{Key}] SaveState timed out waiting for the IO lock (inference in progress?)");
            return false;
        }
        try
        {
            _savedHistoryCount = _history.Count;
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Truncates the transcript back to the last SaveState snapshot.</summary>
    public Task<bool> RewindAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ioLock.Wait(TimeSpan.FromSeconds(30)))
        {
            _logger.Warn("ProcessSession", $"[{Key}] Rewind timed out waiting for the IO lock (inference in progress?)");
            return Task.FromResult(false);
        }
        try
        {
            if (_savedHistoryCount < 0)
            {
                _logger.Warn("ProcessSession", $"[{Key}] Rewind: no saved state");
                return Task.FromResult(false);
            }
            if (_savedHistoryCount > _history.Count)
            {
                _logger.Warn("ProcessSession", $"[{Key}] Rewind: saved state {_savedHistoryCount} beyond history {_history.Count}");
                return Task.FromResult(false);
            }
            _history.RemoveRange(_savedHistoryCount, _history.Count - _savedHistoryCount);
            _logger.Info("ProcessSession", $"[{Key}] Rewound to saved state ({_history.Count} messages)");
            return Task.FromResult(true);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Clears the transcript + prefix; caller must re-prefill.</summary>
    /// <exception cref="TimeoutException">When an in-flight inference holds the IO lock.</exception>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ioLock.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException($"[{Key}] Reset timed out waiting for the IO lock (inference in progress?)");
        try
        {
            _history.Clear();
            _prefix = string.Empty;
            _savedHistoryCount = -1;
            IsPrefilled = false;
            _logger.Info("ProcessSession", $"[{Key}] Transcript reset");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Removes messages appended by a failed turn, tolerating concurrent truncation.</summary>
    private void RemoveAppended(List<ChatMessage> appended, int expectedStartIndex)
    {
        var start = Math.Min(expectedStartIndex, _history.Count);
        _history.RemoveRange(start, Math.Min(appended.Count, _history.Count - start));
    }

    // ── Transport ───────────────────────────────────────

    private JsonObject BuildChatPayload(IReadOnlyList<ChatMessage> turn, int maxTokens)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(_prefix))
            messages.Add(BuildOutboundMessage(new ChatMessage { Role = "system", Content = _prefix }));
        foreach (var msg in _history)
            messages.Add(BuildOutboundMessage(msg));
        foreach (var msg in turn)
            messages.Add(BuildOutboundMessage(msg));

        return new JsonObject
        {
            ["model"] = ModelId,
            ["messages"] = messages,
            ["stream"] = true, // always stream internally — uniform parsing path
            ["max_tokens"] = maxTokens,
        };
    }

    /// <summary>Serializes one message; image parts become base64 data-URI image_url entries.</summary>
    private static JsonNode BuildOutboundMessage(ChatMessage msg)
    {
        if (!msg.HasImages)
            return new JsonObject { ["role"] = msg.Role, ["content"] = msg.Content };

        var parts = new JsonArray();
        if (msg.Content.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = msg.Content });
        foreach (var img in msg.Images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{img.MimeType};base64,{Convert.ToBase64String(img.Data)}"
                }
            });
        }
        return new JsonObject { ["role"] = msg.Role, ["content"] = parts };
    }

    private async Task<HttpResponseMessage> PostChatAsync(JsonObject payload, bool stream, CancellationToken ct)
    {
        var baseUrl = await _host.EnsureStartedUrlAsync(ModelId, ct).ConfigureAwait(false);
        payload["stream"] = stream;
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return await _http.PostAsync($"{baseUrl.TrimEnd('/')}/v1/chat/completions", content, ct).ConfigureAwait(false);
    }

    /// <summary>Streams the child's SSE chat stream, yielding content deltas.</summary>
    private async IAsyncEnumerable<string> StreamDeltasAsync(
        JsonObject payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await PostChatAsync(payload, stream: true, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Child chat request failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 300)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            var delta = ExtractDeltaContent(data);
            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }

    /// <summary>Extracts choices[0].delta.content (or choices[0].message.content) from one SSE chunk.</summary>
    private static string? ExtractDeltaContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var msgContent)
                && msgContent.ValueKind == JsonValueKind.String)
                return msgContent.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ClampMaxTokens(int? requested)
        => Math.Clamp(requested ?? 512, 1, 32768);

    private static int EstimateTokenCount(string text) => text.Length / 4;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _ioLock.Dispose();
    }
}
