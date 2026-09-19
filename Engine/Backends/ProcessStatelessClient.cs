using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Stateless (no transcript) inference against a child llama-server process model.
/// Mirrors the in-process stateless path (fresh executor per call): each request sends
/// exactly the incoming messages — nothing accumulated, nothing persisted.
/// </summary>
internal sealed class ProcessStatelessClient
{
    private readonly IProcessModelHost _host;
    private readonly HttpClient _http;

    public ProcessStatelessClient(IProcessModelHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // SSE stays open
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ECAssistantLLM/1.0");
    }

    /// <summary>
    /// Sends the turn messages to the child and yields content deltas from its SSE stream.
    /// </summary>
    public async IAsyncEnumerable<string> InferStatelessAsync(
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        Models.ChatCompletionRequest request,
        string? grammar,
        int maxTokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = ProcessPayloadFactory.Build(modelId, messages, maxTokens, request, grammar, stream: true);
        var baseUrl = await _host.EnsureStartedUrlAsync(modelId, ct).ConfigureAwait(false);
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{baseUrl.TrimEnd('/')}/v1/chat/completions", content, ct)
            .ConfigureAwait(false);
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

            var delta = ProcessSession.ExtractDeltaContent(data);
            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }
}
