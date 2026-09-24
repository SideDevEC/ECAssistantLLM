using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Models;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

/// <summary>
/// Structured mode on process-backend models: grammar + enable_thinking=false reach
/// the child payload, and the max_tokens pass-through (v15: config-driven budget).
/// </summary>
public sealed class ProcessSessionStructuredTests
{
    private static ModelConfig ProcessModel() => new()
    {
        Id = "bonsai", Path = "/nonexistent/bonsai.gguf", ContextSize = 4096, Backend = "process"
    };

    private static LlmServerConfig Config() => new()
    {
        Server = new ServerSection { MaxSessions = 4, Port = 8420 },
        Models = new List<ModelConfig> { ProcessModel() }
    };

    /// <summary>Host stub that "starts" instantly — the URL points at the test listener.</summary>
    private sealed class FakeProcessHost : IProcessModelHost
    {
        private readonly string _url;
        public FakeProcessHost(string url) => _url = url;
        public IReadOnlyList<ProcessModelInstance> Instances => Array.Empty<ProcessModelInstance>();
        public Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
            => throw new InvalidOperationException("not used by ProcessSession");
        public Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default) => Task.FromResult(_url);
        public Task StopAllAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal SSE child stub: records the request body, streams two deltas, closes.
    /// </summary>
    private sealed class StubChild : IDisposable
    {
        public string? LastBody;
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public StubChild()
        {
            var port = FindFreePort();
            Url = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/");
            _listener.Start();
            Task.Run(() =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { return; }
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    LastBody = reader.ReadToEnd();
                    // one delta, then DONE — enough for InferAsync to complete
                    var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"thinking\\\":\\\"t\\\",\\\"answer\\\":\\\"a\\\"}\"}}]}\n\n" +
                              "data: [DONE]\n\n";
                    var bytes = Encoding.UTF8.GetBytes(sse);
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static (ProcessSession Session, StubChild Child) NewSession()
    {
        var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        var session = registry.Create("client-a", "s1", ProcessModel());
        return (session, child);
    }

    private static ChatCompletionRequest ChatRequest(int? maxTokens = null) => new()
    {
        Model = "bonsai",
        SessionId = "s1",
        Stream = false,
        MaxTokens = maxTokens,
        Messages = new List<ChatMessage> { new() { Role = "user", Content = "hello" } }
    };

    [Fact]
    public async Task InferAsync_WithGrammar_SendsGrammarAndThinkingDisabled()
    {
        using var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        using var session = registry.Create("client-a", "s1", ProcessModel());

        var deltas = new List<string>();
        await foreach (var d in session.InferAsync(ChatRequest().Messages, ChatRequest(),
            CancellationToken.None, grammar: "root ::= \"x\""))
            deltas.Add(d);

        Assert.NotNull(child.LastBody);
        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.NotNull(payload["grammar"]);
        Assert.Equal("root ::= \"x\"", payload["grammar"]!.ToString());
        Assert.False(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.True(deltas.Count > 0);
    }

    [Fact]
    public async Task InferAsync_WithGrammar_RespectsRequestedMaxTokens()
    {
        using var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        using var session = registry.Create("client-a", "s1", ProcessModel());

        await foreach (var _ in session.InferAsync(ChatRequest(maxTokens: 4096).Messages, ChatRequest(maxTokens: 4096),
            CancellationToken.None, grammar: "root ::= \"x\""))
        { }

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        // v15 (Emre, 2026-09-24): the former hard 256 clamp was removed — the request
        // budget passes through (config-driven max_tokens law).
        Assert.Equal(4096, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task InferAsync_WithoutGrammar_OmitsStructuredFields()
    {
        using var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        using var session = registry.Create("client-a", "s1", ProcessModel());

        await foreach (var _ in session.InferAsync(ChatRequest(maxTokens: 4096).Messages, ChatRequest(maxTokens: 4096)))
        { }

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.Null(payload["grammar"]);
        Assert.Null(payload["chat_template_kwargs"]);
        // non-structured keeps the existing 512-default clamp behavior
        Assert.Equal(4096, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task EvaluateAsync_FeedsTransientTurn_TranscriptUntouched()
    {
        // v15 (Emre, 2026-09-24): KV-hygiene primitive — prompt-only feed with
        // bounded sampling; the transient turn must NOT enter the session transcript.
        using var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        using var session = registry.Create("client-a", "s1", ProcessModel());

        var (ok, _) = await session.EvaluateAsync("repaired envelope text", maxTokens: 1);
        Assert.True(ok);

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.Equal(1, payload["max_tokens"]!.GetValue<int>());

        // Transcript untouched: the next real turn's payload must not contain the
        // transient evaluate text as a committed message.
        var before = child.LastBody;
        await foreach (var _ in session.InferAsync(ChatRequest().Messages, ChatRequest()))
        { }
        var nextPayload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.NotEqual(before, child.LastBody);
        Assert.DoesNotContain("repaired envelope text", nextPayload.ToString());
    }

    [Fact]
    public async Task EvaluateAsync_EmptyText_SucceedsWithoutChildCall()
    {
        using var child = new StubChild();
        var registry = new ProcessSessionRegistry(new FakeProcessHost(child.Url), Config(), new ServerLogger(LogLevel.Error));
        using var session = registry.Create("client-a", "s1", ProcessModel());

        var (ok, _) = await session.EvaluateAsync("");
        Assert.True(ok);
        Assert.Null(child.LastBody); // no child round-trip for empty text
    }
}
