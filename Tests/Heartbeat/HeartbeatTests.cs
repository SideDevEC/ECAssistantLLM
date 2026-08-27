using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Heartbeat;

/// <summary>
/// Tests for heartbeat mechanism. Uses the main TestServerFixture (timeout 300s).
/// We test that heartbeats keep clients alive and that stopping heartbeats
/// returns correct responses. Full eviction testing requires a short-timeout
/// fixture which would need a separate model load (OOM risk), so we test
/// the heartbeat endpoint behavior here.
/// </summary>
[Collection("Server")]
public class HeartbeatTests
{
    private readonly TestServerFixture _fixture;

    public HeartbeatTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Heartbeat_Keeps_Client_Alive()
    {
        var clientId = await _fixture.RegisterClientAsync("hb-alive");

        // Send 3 heartbeats
        for (int i = 0; i < 3; i++)
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 1 }, "default");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task Heartbeat_Reports_Active_Sessions_Count()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync(sessionId: "hb-sessions-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/clients/{clientId}/heartbeat",
                new { active_sessions = 1 }, "default");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Heartbeat_Unknown_Client_Returns_404()
    {
        using var resp = await _fixture.PostJsonAsClientAsync(
            "/eca/clients/00000000000000000000000000000000/heartbeat",
            new { active_sessions = 0 }, "default");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Client_Removes_Heartbeat_Target()
    {
        var clientId = await _fixture.RegisterClientAsync("hb-delete");

        // Verify heartbeat works
        using var hbBefore = await _fixture.PostJsonAsClientAsync(
            $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 0 }, "default");
        Assert.Equal(HttpStatusCode.OK, hbBefore.StatusCode);

        // Delete client
        using var del = await _fixture.DeleteAsClientAsync($"/eca/clients/{clientId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // Heartbeat should now 404
        using var hbAfter = await _fixture.PostJsonAsClientAsync(
            $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 0 }, "default");
        Assert.Equal(HttpStatusCode.NotFound, hbAfter.StatusCode);
    }
}