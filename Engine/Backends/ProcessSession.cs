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

    /// <summary>v14.9: toolset pinning fingerprint (see ToolsetFingerprint). Transcript-
    /// backed sessions don't hold the KV cache, so a mismatch just updates the pin —
    /// the child's prefix cache rebuilds naturally. Null = not pinned.</summary>
    public string? ToolsHash { get; set; }

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
    /// When <paramref name="grammar"/> is set (structured mode), the child is asked to
    /// constrain output to that GBNF grammar with thinking disabled — mirroring the
    /// in-process sampling-pipeline grammar (v14.8.2).
    /// </summary>
    public async IAsyncEnumerable<string> InferAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        string? grammar = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage> appended = new();
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        // v-fix: the IO lock is now held for the WHOLE turn (append → stream → commit),
        // matching the documented contract "serializes transcript mutations
        // (SaveState/Rewind/Reset) against streaming inference". Previously the lock was
        // released during streaming, so a concurrent Reset() could wipe the transcript
        // mid-inference and the assistant reply was still committed afterwards.
        List<ChatMessage> snapshot;
        try
        {
            snapshot = _history.ToList();
            _history.AddRange(messages);
            appended.AddRange(messages);
        }
        catch
        {
            _ioLock.Release();
            throw;
        }

        LastActivity = DateTime.UtcNow;

        // Structured mode mirrors the in-process structured cap (CreateStructuredInferenceParams:
        // clamp to 256) — envelopes are small; early-stop in the router handles the actual stop.
        var maxTokens = grammar != null
            ? Math.Clamp(Math.Min(request.MaxTokens ?? 256, 256), 1, 256)
            : ClampMaxTokens(request.MaxTokens);
        var payload = BuildChatPayload(messages, maxTokens, grammar, request);

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
                    // stays consistent for the next attempt. The IO lock is already held
                    // (acquired above and kept for the whole turn) — no re-acquire here.
                    RemoveAppended(appended, snapshot.Count);
                    throw;
                }

                replySb.Append(delta);
                yield return delta;
            }

            var assistantReply = replySb.ToString();

            // Commit the assistant turn to the transcript. The appended user messages stay
            // (they are part of the conversation); only a FAILED turn rolls them back.
            if (!string.IsNullOrEmpty(assistantReply))
                _history.Add(new ChatMessage { Role = "assistant", Content = assistantReply });
            if (!string.IsNullOrEmpty(_prefix)) IsPrefilled = true;
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
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

    internal JsonObject BuildChatPayload(IReadOnlyList<ChatMessage> turn, int maxTokens, string? grammar = null, ChatCompletionRequest? sampling = null)
    {
        // Prefix + history + turn — transcript-backed: the child's slot/prefix cache
        // absorbs the re-prefill of the shared prefix.
        var all = new List<ChatMessage>(turn.Count + _history.Count + 1);
        if (!string.IsNullOrEmpty(_prefix))
            all.Add(new ChatMessage { Role = "system", Content = _prefix });
        all.AddRange(_history);
        all.AddRange(turn);

        return ProcessPayloadFactory.Build(ModelId, all, maxTokens, sampling, grammar, stream: true);
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
    internal static string? ExtractDeltaContent(string json)
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
        // Bounded best-effort: avoid tearing down mid-commit — a concurrent Dispose
        // during an in-flight inference could otherwise corrupt the child request.
        if (!_ioLock.Wait(TimeSpan.FromSeconds(30)))
        {
            _logger.Warn("ProcessSession", $"[{Key}] Dispose: inference still in progress after 30s — disposing anyway");
        }
        try
        {
            _http.Dispose();
        }
        finally
        {
            _ioLock.Release();
            _ioLock.Dispose();
        }
    }
}
