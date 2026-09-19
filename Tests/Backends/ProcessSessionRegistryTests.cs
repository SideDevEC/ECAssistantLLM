using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine.Backends;
using ECAssistant.LLM.Models;
using Xunit;

namespace ECAssistant.LLM.Tests.Backends;

public sealed class ProcessSessionRegistryTests
{
    private static LlmServerConfig Config(int maxSessions = 8) => new()
    {
        Server = new ServerSection { MaxSessions = maxSessions, Port = 8420 },
        Models = new List<ModelConfig>
        {
            new() { Id = "bonsai", Path = "/nonexistent/bonsai.gguf", ContextSize = 4096, Backend = "process" }
        }
    };

    private static ModelConfig ProcessModel() => new()
    {
        Id = "bonsai", Path = "/nonexistent/bonsai.gguf", ContextSize = 4096, Backend = "process"
    };

    /// <summary>Stub host — never starts a real child; url resolution fails fast.</summary>
    private sealed class StubProcessHost : IProcessModelHost
    {
        public IReadOnlyList<ProcessModelInstance> Instances => Array.Empty<ProcessModelInstance>();

        public Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
            => throw new HttpRequestException("stub: no child process in unit tests");

        public Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default)
            => throw new HttpRequestException("stub: no child process in unit tests");

        public Task StopAllAsync() => Task.CompletedTask;
    }

    private static ProcessSessionRegistry NewRegistry(LlmServerConfig config)
        => new(new StubProcessHost(), config, new ServerLogger(LogLevel.Error));

    private static ChatCompletionRequest ChatRequest(string sessionId, bool stream = false) => new()
    {
        Model = "bonsai",
        SessionId = sessionId,
        Stream = stream,
        Messages = new List<ChatMessage> { new() { Role = "user", Content = "hello" } }
    };

    [Fact]
    public void Create_ReturnsSessionWithExpectedIdentity()
    {
        using var registry = NewRegistry(Config());
        var session = registry.Create("client-a", "s1", ProcessModel());
        Assert.Equal("client-a:s1", session.Key);
        Assert.Equal("bonsai", session.ModelId);
        Assert.Equal((uint)4096, session.ContextSize);
        Assert.Equal(0, session.EstimatedVramMb); // KV lives in the child process
        Assert.False(session.IsPrefilled);
    }

    [Fact]
    public void Create_DuplicateSession_Throws()
    {
        using var registry = NewRegistry(Config());
        registry.Create("client-a", "s1", ProcessModel());
        Assert.Throws<InvalidOperationException>(() => registry.Create("client-a", "s1", ProcessModel()));
    }

    [Fact]
    public void Create_SameSessionIdDifferentClients_BothAllowed()
    {
        using var registry = NewRegistry(Config());
        registry.Create("client-a", "s1", ProcessModel());
        registry.Create("client-b", "s1", ProcessModel());
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Create_ExceedsMaxSessions_Throws()
    {
        using var registry = NewRegistry(Config(maxSessions: 1));
        registry.Create("client-a", "s1", ProcessModel());
        Assert.Throws<InvalidOperationException>(() => registry.Create("client-a", "s2", ProcessModel()));
    }

    [Fact]
    public void Destroy_RemovesSession()
    {
        using var registry = NewRegistry(Config());
        registry.Create("client-a", "s1", ProcessModel());
        Assert.True(registry.Destroy("client-a", "s1"));
        Assert.False(registry.Contains("client-a", "s1"));
        Assert.False(registry.Destroy("client-a", "s1")); // second destroy: not found
    }

    [Fact]
    public void DestroyClient_RemovesOnlyThatClientsSessions()
    {
        using var registry = NewRegistry(Config());
        registry.Create("client-a", "s1", ProcessModel());
        registry.Create("client-a", "s2", ProcessModel());
        registry.Create("client-b", "s3", ProcessModel());
        Assert.Equal(2, registry.DestroyClient("client-a"));
        Assert.Equal(1, registry.Count);
        Assert.True(registry.Contains("client-b", "s3"));
    }

    [Fact]
    public void Get_UnknownSession_ReturnsNull()
    {
        using var registry = NewRegistry(Config());
        Assert.Null(registry.Get("client-a", "nope"));
    }

    [Fact]
    public async Task Rewind_WithoutSavedState_ReturnsFalse()
    {
        using var registry = NewRegistry(Config());
        var session = registry.Create("client-a", "s1", ProcessModel());
        Assert.False(await session.RewindAsync());
    }

    [Fact]
    public async Task FailedInference_RollsBackAppendedMessages()
    {
        using var registry = NewRegistry(Config());
        var session = registry.Create("client-a", "s1", ProcessModel());

        var before = session.ApproxTokenCount;
        var request = ChatRequest("s1");
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in session.InferAsync(request.Messages, request, CancellationToken.None))
            {
            }
        });
        Assert.Equal(before, session.ApproxTokenCount); // transcript rolled back, no residue
    }

    [Fact]
    public void SaveStateResetAndStatus_Consistent()
    {
        using var registry = NewRegistry(Config());
        var session = registry.Create("client-a", "s1", ProcessModel());
        Assert.True(session.SaveState());
        session.Reset();
        Assert.False(session.IsPrefilled);
        Assert.Equal(0, session.ApproxTokenCount);
    }
}
