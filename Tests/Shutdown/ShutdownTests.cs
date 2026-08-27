using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Shutdown;

/// <summary>
/// Tests for the /eca/shutdown endpoint.
/// Uses the main TestServerFixture (shutdown_on_last_client: false, so server stays alive).
/// We test that the shutdown endpoint returns 200 and disconnects the calling client.
/// </summary>
[Collection("Server")]
public class ShutdownTests
{
    private readonly TestServerFixture _fixture;

    public ShutdownTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Shutdown_Endpoint_Returns_200()
    {
        var clientId = await _fixture.RegisterClientAsync("shutdown-test");

        using var resp = await _fixture.PostJsonAsClientAsync("/eca/shutdown", new { }, clientId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Server responds with ok=true (client disconnected, server stays alive since shutdown_on_last_client=false)
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Shutdown_Disconnects_Calling_Client()
    {
        var clientId = await _fixture.RegisterClientAsync("shutdown-disconnect");

        // Verify client exists via heartbeat
        var hbBefore = await _fixture.PostJsonAsClientAsync(
            $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 0 }, "default");
        Assert.Equal(HttpStatusCode.OK, hbBefore.StatusCode);
        hbBefore.Dispose();

        // Send shutdown for this client
        using var resp = await _fixture.PostJsonAsClientAsync("/eca/shutdown", new { }, clientId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Client should now be disconnected — heartbeat should return 404
        using var hbAfter = await _fixture.PostJsonAsClientAsync(
            $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 0 }, "default");
        Assert.Equal(HttpStatusCode.NotFound, hbAfter.StatusCode);
    }

    [Fact]
    public async Task Shutdown_With_No_Client_Header_Still_Works()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/shutdown", new { });

        // Server defaults to "default" client — disconnects it
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}