using System.Net;
using System.Net.Http;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Stateless 1:1 proxy: forwards an incoming HttpListener request to a target base URL
/// (e.g. a llama-server child process) and streams the response back — including SSE.
/// Stateless utility — no mutable state.
/// </summary>
public static class ProxyRequestHandler
{
    // Stateless utility — no mutable state

    private static readonly HttpClient Client = CreateClient();

    /// <summary>Forwards the request and streams the response back to the client.</summary>
    public static async Task ForwardAsync(HttpListenerContext ctx, string targetBaseUrl, byte[]? rawBody = null, CancellationToken ct = default)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        var target = new Uri(targetBaseUrl.TrimEnd('/') + req.Url!.PathAndQuery);

        using var forward = new HttpRequestMessage(new HttpMethod(req.HttpMethod), target);
        // Prefer the buffered raw body cached by the router (HttpListener InputStream
        // is already consumed when handlers parsed the request). Fall back to the stream.
        if (rawBody is { Length: > 0 })
        {
            var content = new ByteArrayContent(rawBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                req.ContentType ?? "application/json");
            forward.Content = content;
        }
        else if (req.HasEntityBody)
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms, ct);
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                req.ContentType ?? "application/json");
            forward.Content = content;
        }
        // Auth is handled by the outer server; do not forward client headers.

        using var response = await Client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct);
        res.StatusCode = (int)response.StatusCode;

        var contentType = response.Content.Headers.ContentType?.ToString();
        if (contentType != null) res.ContentType = contentType;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(res.OutputStream, ct);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // SSE stays open
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ECAssistantLLM/1.0");
        return client;
    }
}
