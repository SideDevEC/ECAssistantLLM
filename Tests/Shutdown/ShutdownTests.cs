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
[Trait("Category","E2E")]
    public class ShutdownTests
{
    private readonly TestServerFixture _fixture;

    public ShutdownTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Shutdown_Endpoint_Returns_200()
    {
        // Keep the shared server alive — register a second client so the shutdown
        // endpoint doesn't wind down the server (ClientCount reaches 0 after disconnect).
        var keepAlive = await _fixture.RegisterClientAsync("shutdown-keepalive");
        var clientId = await _fixture.RegisterClientAsync("shutdown-test");

        using var resp = await _fixture.PostJsonAsClientAsync("/eca/shutdown", new { }, clientId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Server responds with ok=true (client disconnected, server stays alive since shutdown_on_last_client=false)
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Shutdown_With_No_Client_Header_Returns_401()
    {
        // No registered X-Client-Id — /eca/shutdown requires authentication.
        using var resp = await _fixture.PostRawAsync("/eca/shutdown", "{}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}