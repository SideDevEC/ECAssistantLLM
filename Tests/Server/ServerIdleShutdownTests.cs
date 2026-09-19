using ECAssistant.LLM.Config;
using ECAssistant.LLM.Engine;
using ECAssistant.LLM.Server;
using Xunit;

namespace ECAssistant.LLM.Tests.Server;

/// <summary>
/// Regression for the 2026-09-18/19 zombie: HttpListener.GetContextAsync is not
/// cancellable, so a server with zero traffic parked in the pending accept forever
/// after cts.Cancel() — 21 h uptime, 854 shutdown re-attempts, exit only when an
/// unrelated request unblocked the accept. RunAsync must now exit promptly on
/// cancellation with zero traffic (listener stop registration).
/// </summary>
public sealed class ServerIdleShutdownTests
{
    private static LlmServerConfig Config(int port) => new()
    {
        Server = new ServerSection { Host = "127.0.0.1", Port = port, MaxSessions = 2 },
        // MultiModelHost requires at least one chat model id (weights are never loaded
        // here — LoadAllAsync is not called in this test)
        Models = new List<ModelConfig> { new() { Id = "dummy", Path = "/tmp/dummy.gguf" } }
    };

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static LlmHttpServer NewServer(int port, out CancellationTokenSource cts)
    {
        var config = Config(port);
        var logger = new ServerLogger(LogLevel.Error);
        var modelHost = new MultiModelHost(config, logger, "/tmp");
        var scheduler = new InferenceScheduler(logger);
        var vramBudget = new VramBudget(config);
        var sessionRegistry = new SessionRegistry(modelHost, scheduler, config, logger, vramBudget);
        cts = new CancellationTokenSource();
        var clientManager = new ClientManager(sessionRegistry, config, logger, onLastClientDisconnected: null);
        return new LlmHttpServer(config, modelHost, sessionRegistry, scheduler, vramBudget,
            clientManager, logger, cts);
    }

    [Fact]
    public async Task RunAsync_ExitsOnCancellation_WithZeroTraffic()
    {
        var port = FindFreePort();
        var server = NewServer(port, out var cts);
        try
        {
            var runTask = server.RunAsync(cts.Token);

            // Give the listener a moment to start, then cancel with no request ever sent
            await Task.Delay(300);
            cts.Cancel();

            var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(completed, runTask),
                "RunAsync must exit on cancellation even with zero pending requests");
            await runTask; // propagate exceptions if any
        }
        finally
        {
            server.Dispose();
            cts.Dispose();
        }
    }
}
