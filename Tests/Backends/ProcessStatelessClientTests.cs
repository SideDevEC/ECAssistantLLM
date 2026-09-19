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
/// Stateless process inference: sampling parity (explicit in-process defaults, never
/// child defaults), grammar/thinking fields, and max_tokens clamp.
/// </summary>
public sealed class ProcessStatelessClientTests
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

    private sealed class FakeProcessHost : IProcessModelHost
    {
        private readonly string _url;
        public FakeProcessHost(string url) => _url = url;
        public IReadOnlyList<ProcessModelInstance> Instances => Array.Empty<ProcessModelInstance>();
        public Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
            => throw new InvalidOperationException("not used by ProcessStatelessClient");
        public Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default) => Task.FromResult(_url);
        public Task StopAllAsync() => Task.CompletedTask;
    }

    private sealed class StubChild : IDisposable
    {
        public string? LastBody;
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public StubChild()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
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
                    var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
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

    private static ChatCompletionRequest ChatRequest(
        float? temperature = null, float? topP = null, int? topK = null,
        float? repeatPenalty = null, int? maxTokens = null, List<string>? stop = null) => new()
    {
        Model = "bonsai",
        Stream = false,
        Temperature = temperature,
        TopP = topP,
        TopK = topK,
        RepeatPenalty = repeatPenalty,
        MaxTokens = maxTokens,
        Stop = stop,
        Messages = new List<ChatMessage> { new() { Role = "user", Content = "hello" } }
    };

    [Fact]
    public async Task InferStatelessAsync_NoSamplingFields_UsesInProcessDefaults()
    {
        using var child = new StubChild();
        var client = new ProcessStatelessClient(new FakeProcessHost(child.Url));
        await foreach (var _ in client.InferStatelessAsync("bonsai", ChatRequest().Messages, ChatRequest(), null, 512))
        { }

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        // Same defaults as in-process CreateInferenceParams — child defaults must never leak
        Assert.Equal(0.3m, payload["temperature"]!.GetValue<decimal>());
        Assert.Equal(0.95m, payload["top_p"]!.GetValue<decimal>());
        Assert.Equal(40, payload["top_k"]!.GetValue<int>());
        Assert.Equal(1.1m, payload["repeat_penalty"]!.GetValue<decimal>());
        Assert.Equal(512, payload["max_tokens"]!.GetValue<int>());
        Assert.Null(payload["grammar"]);
        Assert.Null(payload["chat_template_kwargs"]);
    }

    [Fact]
    public async Task InferStatelessAsync_Structured_SetsGrammarAndClampsTokens()
    {
        using var child = new StubChild();
        var client = new ProcessStatelessClient(new FakeProcessHost(child.Url));
        await foreach (var _ in client.InferStatelessAsync(
            "bonsai", ChatRequest(maxTokens: 4096).Messages, ChatRequest(maxTokens: 4096),
            grammar: "root ::= \"x\"", maxTokens: 256))
        { }

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.Equal("root ::= \"x\"", payload["grammar"]!.ToString());
        Assert.False(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(256, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task InferStatelessAsync_PassesSamplingAndStopThrough()
    {
        using var child = new StubChild();
        var client = new ProcessStatelessClient(new FakeProcessHost(child.Url));
        await foreach (var _ in client.InferStatelessAsync(
            "bonsai",
            ChatRequest(temperature: 0.7f, topP: 0.9f, topK: 30, repeatPenalty: 1.05f,
                maxTokens: 128, stop: new List<string> { "END" }).Messages,
            ChatRequest(temperature: 0.7f, topP: 0.9f, topK: 30, repeatPenalty: 1.05f,
                maxTokens: 128, stop: new List<string> { "END" }),
            null, 128))
        { }

        var payload = JsonNode.Parse(child.LastBody!)!.AsObject();
        Assert.Equal(0.7m, payload["temperature"]!.GetValue<decimal>());
        Assert.Equal(0.9m, payload["top_p"]!.GetValue<decimal>());
        Assert.Equal(30, payload["top_k"]!.GetValue<int>());
        Assert.Equal(1.05m, payload["repeat_penalty"]!.GetValue<decimal>());
        Assert.Equal(128, payload["max_tokens"]!.GetValue<int>());
        Assert.Equal("END", payload["stop"]![0]!.ToString());
    }
}
