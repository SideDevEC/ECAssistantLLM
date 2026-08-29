using System.Net;
using System.Net.Sockets;
using System.Text;
using ECAssistant.LLM.Server;

namespace ECAssistant.LLM.Tests.Server;

/// <summary>
/// SSE framing tests over a real loopback HttpListener — no model weights involved.
/// </summary>
public class SseStreamerTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _client;
    private readonly string _prefix;
    private CancellationTokenSource _cts = new();

    public SseStreamerTests()
    {
        var port = GetFreePort();
        _prefix = $"http://localhost:{port}/sse-test/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose()
    {
        _client.Dispose();
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        _cts.Dispose();
    }

    /// <summary>
    /// Accept one request, hand the response to <paramref name="serve"/>, return the raw body.
    /// </summary>
    private async Task<string> ServeOnceAsync(Func<HttpListenerResponse, CancellationToken, Task> serve)
    {
        var cts = _cts;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            await serve(ctx.Response, cts.Token);
        });

        var resp = await _client.GetAsync(_prefix);
        var body = await resp.Content.ReadAsStringAsync();
        await serverTask;
        return body;
    }

    [Fact]
    public async Task StreamAsync_EmitsDeltaFinishAndDone()
    {
        var body = await ServeOnceAsync((res, ct) =>
            SseStreamer.StreamAsync(res, TokenStream("Hello", " world"), "main", ct));

        Assert.Contains("data: ", body);
        Assert.Contains("\"chat.completion.chunk\"", body);
        Assert.Contains("\"content\":\"Hello\"", body);
        Assert.Contains("\"content\":\" world\"", body);
        Assert.Contains("\"finish_reason\":\"stop\"", body);
        Assert.EndsWith("data: [DONE]\n\n", body);

        // Every data line is followed by an empty line (event boundary)
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("data: ") && lines[i] != "data: [DONE]")
                Assert.Equal("", lines[i + 1].TrimEnd('\r'));
        }
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesTextCompletionObject()
    {
        var body = await ServeOnceAsync((res, ct) =>
            SseStreamer.StreamCompletionAsync(res, TokenStream("Hi"), "main", ct));

        Assert.Contains("\"text_completion\"", body);
        Assert.Contains("\"text\":\"Hi\"", body);
        Assert.Contains("\"finish_reason\":\"stop\"", body);
        Assert.Contains("data: [DONE]", body);
        Assert.DoesNotContain("chat.completion.chunk", body);
    }

    [Fact]
    public async Task StreamAsync_AllEventsShareChunkId()
    {
        var body = await ServeOnceAsync((res, ct) =>
            SseStreamer.StreamAsync(res, TokenStream("a", "b", "c"), "main", ct));

        var ids = body.Split('\n')
            .Where(l => l.StartsWith("data: ") && l != "data: [DONE]")
            .Select(l => Extract(l, "\"id\":\""))
            .ToList();
        Assert.Equal(4, ids.Count); // 3 deltas + finish
        Assert.All(ids, id => Assert.Equal(ids[0], id));
    }

    [Fact]
    public async Task WriteJsonAsync_SetsStatusAndContentType()
    {
        var (statusCode, contentType, body) = await ServeCaptureAsync((ctx, ct) =>
            SseStreamer.WriteJsonAsync(ctx.Response, new { ok = true }, 418));

        Assert.Equal(418, statusCode);
        Assert.Equal("application/json", contentType);
        Assert.Contains("\"ok\":true", body);
    }

    [Fact]
    public async Task ReadJsonAsync_SmallBody_Deserializes()
    {
        var (statusCode, _, body) = await ServeCaptureAsync(async (ctx, ct) =>
        {
            ctx.Request.Headers["Content-Type"] = "application/json";
            var parsed = await SseStreamer.ReadJsonAsync<Dictionary<string, string>>(ctx.Request, ct);
            Assert.NotNull(parsed);
            Assert.Equal("bar", parsed!["foo"]);
            await SseStreamer.WriteJsonAsync(ctx.Response, new { received = true });
        }, sendBody: "{\"foo\":\"bar\"}");

        Assert.Equal(200, statusCode);
        Assert.Contains("\"received\":true", body);
    }

    [Fact]
    public async Task ReadJsonAsync_OversizedBody_ReturnsDefault()
    {
        // Declare + send a body larger than the cap — must not be buffered/deserialized.
        var oversized = new string('x', (int)SseStreamer.MaxRequestBodyBytes + 1024);
        var (statusCode, _, body) = await ServeCaptureAsync(async (ctx, ct) =>
        {
            var parsed = await SseStreamer.ReadJsonAsync<Dictionary<string, string>>(ctx.Request, ct);
            Assert.Null(parsed); // rejected before buffering
            await SseStreamer.WriteJsonAsync(ctx.Response, new { rejected = true });
        }, sendBody: oversized);

        Assert.Equal(200, statusCode);
        Assert.Contains("\"rejected\":true", body);
    }

    // ── Helpers ───────────────────────────────────────────────

    private static async IAsyncEnumerable<string> TokenStream(params string[] tokens)
    {
        foreach (var t in tokens)
            yield return t;
    }

    private static string Extract(string line, string prefix)
    {
        var idx = line.IndexOf(prefix, StringComparison.Ordinal);
        var start = idx + prefix.Length;
        return line[start..line.IndexOf('"', start)];
    }

    private async Task<(int StatusCode, string? ContentType, string Body)> ServeCaptureAsync(
        Func<HttpListenerContext, CancellationToken, Task> serve, string? sendBody = null)
    {
        var cts = _cts;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            await serve(ctx, cts.Token);
        });

        using var resp = sendBody == null
            ? await _client.GetAsync(_prefix)
            : await _client.PostAsync(_prefix, new StringContent(sendBody, Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        await serverTask;
        return ((int)resp.StatusCode, resp.Content?.Headers.ContentType?.MediaType, body);
    }

    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
