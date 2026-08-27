using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Logging;

/// <summary>
/// Tests that the server creates log files and writes meaningful entries
/// for startup, client registration, session creation, and errors.
/// </summary>
[Collection("Server")]
public class ServerLogTests
{
    private readonly TestServerFixture _fixture;

    public ServerLogTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public void Log_File_Exists()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log");
        Assert.True(File.Exists(logPath), $"Log file should exist at {logPath}");
    }

    [Fact]
    public async Task Log_File_Contains_Client_Registration_Entry()
    {
        var clientId = await _fixture.RegisterClientAsync("log-test-client");
        var logPath = Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log");

        // Give the logger a moment to flush
        await Task.Delay(200);

        var logContent = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Registered client", logContent);
    }

    [Fact]
    public async Task Log_File_Contains_Session_Creation_Entry()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync(
            sessionId: "log-test-session-" + Guid.NewGuid().ToString("N")[..8],
            clientName: "log-sess-client");

        var logPath = Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log");
        await Task.Delay(200);

        var logContent = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Created session", logContent);

        // Cleanup
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
    }

    [Fact]
    public async Task Log_File_Contains_Error_Entry_For_404()
    {
        // Trigger a 404 by requesting a non-existent path
        using var resp = await _fixture.GetAsClientAsync("/eca/does-not-exist");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);

        var logPath = Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log");
        await Task.Delay(200);

        var logContent = await File.ReadAllTextAsync(logPath);
        // Server should log something about the not-found request
        Assert.Contains("Not found", logContent);
    }

    [Fact]
    public async Task Log_File_Contains_Prefill_Entry()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync(
            sessionId: "log-prefill-" + Guid.NewGuid().ToString("N")[..8],
            clientName: "log-pf-client");

        try
        {
            await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill",
                new { text = "Prefill test for logging." }, clientId);

            var logPath = Path.Combine(AppContext.BaseDirectory, "ecassistant-llm-test.log");
            await Task.Delay(200);

            var logContent = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Prefilled", logContent);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }
}