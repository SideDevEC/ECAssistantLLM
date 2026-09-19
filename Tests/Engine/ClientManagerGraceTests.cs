using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Interfaces;
using Xunit;

namespace ECAssistant.LLM.Tests.Engine;

/// <summary>
/// ClientManager shutdown grace: last-client disconnect starts a countdown; a client
/// returning within the window cancels it; expiry fires the callback once.
/// </summary>
public sealed class ClientManagerGraceTests
{
    private static (ClientManager Manager, List<string> Callbacks) Create(int graceSec, int heartbeatTimeoutSec = 90)
    {
        var callbacks = new List<string>();
        var config = new LlmServerConfig
        {
            Server = new ServerSection
            {
                Host = "127.0.0.1",
                Port = 48999,
                HeartbeatTimeoutSec = heartbeatTimeoutSec,
                ShutdownOnLastClient = true,
                ShutdownGraceSec = graceSec
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
    public async Task Disconnect_LastClient_Grace_WaitsThenFires()
    {
        var (manager, callbacks) = Create(graceSec: 1);

        var clientId = manager.Register("grace-test");
        manager.Disconnect(clientId);

        Assert.Empty(callbacks); // not immediate — countdown running

        await Task.Delay(1500);
        Assert.Single(callbacks);
    }

    [Fact]
    public async Task Disconnect_ThenReRegister_ShutdownCancelled()
    {
        var (manager, callbacks) = Create(graceSec: 1);

        var clientId = manager.Disconnect(manager.Register("grace-test")) ? "x" : "x";
        manager.Register("grace-test-return");

        await Task.Delay(1500);
        Assert.Empty(callbacks); // cancelled by the returning client
    }

    [Fact]
    public void Disconnect_GraceZero_FiresImmediately()
    {
        var (manager, callbacks) = Create(graceSec: 0);

        var clientId = manager.Register("immediate-test");
        manager.Disconnect(clientId);

        Assert.Single(callbacks);
    }

    [Fact]
    public async Task GraceCycle_CanRepeatAfterCancel()
    {
        var (manager, callbacks) = Create(graceSec: 1);

        // cycle 1: disconnect (grace starts) → reconnect within window (cancels)
        var first = manager.Register("cycle-1");
        manager.Disconnect(first);
        var returning = manager.Register("cycle-1-return");
        await Task.Delay(1300);
        Assert.Empty(callbacks); // cancelled — client returned in time

        // cycle 2: that client leaves too → everyone gone → grace expires → shutdown
        manager.Disconnect(returning);
        await Task.Delay(1500);
        Assert.Single(callbacks);
    }
}
