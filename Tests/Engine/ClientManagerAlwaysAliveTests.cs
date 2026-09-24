using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Interfaces;
using Xunit;

namespace ECAssistant.LLM.Tests.Engine;

/// <summary>
/// Always-alive contract: the server NEVER self-shuts down. Last-client disconnect
/// (or full eviction) only frees sessions — shutdown happens exclusively via the
/// explicit /eca/shutdown endpoint or a process signal.
/// </summary>
public sealed class ClientManagerAlwaysAliveTests
{
    private static (ClientManager Manager, List<string> Callbacks) Create()
    {
        var callbacks = new List<string>();
        var config = new LlmServerConfig
        {
            Server = new ServerSection
            {
                Host = "127.0.0.1",
                Port = 48999,
                ShutdownOnLastClient = false,
                ShutdownGraceSec = 0
            },
            Models = new List<ModelConfig> { new() { Id = "dummy", Path = "/tmp/dummy.gguf" } }
        };
        var logger = new ServerLogger(LogLevel.Error);
        var modelHost = new MultiModelHost(config, logger, "/tmp");
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);
        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger, vramBudget);
        var manager = new ClientManager(sessionRegistry, config, logger,
            onLastClientDisconnected: () => callbacks.Add("shutdown"));
        return (manager, callbacks);
    }

    [Fact]
    public async Task Disconnect_LastClient_DoesNotTriggerShutdown()
    {
        var (manager, callbacks) = Create();

        var clientId = manager.Register("always-alive");
        manager.Disconnect(clientId);

        // No grace timer, no shutdown — give any hypothetical async path time to fire.
        await Task.Delay(1500);
        Assert.Empty(callbacks);
    }

    [Fact]
    public async Task Disconnect_AllClients_DoesNotTriggerShutdown()
    {
        var (manager, callbacks) = Create();

        var a = manager.Register("aa-1");
        var b = manager.Register("aa-2");
        manager.Disconnect(a);
        manager.Disconnect(b);

        await Task.Delay(1500);
        Assert.Empty(callbacks);
        Assert.Equal(0, manager.ClientCount);
    }

    [Fact]
    public void Reconnect_AfterDisconnect_StillWorks()
    {
        var (manager, callbacks) = Create();

        var first = manager.Register("cycle-1");
        manager.Disconnect(first);
        var second = manager.Register("cycle-2");

        Assert.True(manager.IsValid(second));
        Assert.Equal(1, manager.ClientCount);
        Assert.Empty(callbacks);
    }
}
