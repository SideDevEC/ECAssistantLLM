using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Health;

/// <summary>
/// Tests for GET /eca/health.
/// </summary>
[Collection("Server")]
public class HealthTests
{
    private readonly TestServerFixture _fixture;

    public HealthTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Health_Returns_200_With_Status_Ok()
     {
        using var resp = await _fixture.GetAsClientAsync("/eca/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
     }

    [Fact]
    public async Task Health_Response_Contains_Expected_Fields()
     {
        using var resp = await _fixture.GetAsClientAsync("/eca/health");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("version").GetString()));

        // models_loaded should be an array containing the two loaded models.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("models_loaded").ValueKind);
        Assert.True(root.GetProperty("models_loaded").GetArrayLength() >= 2,
            "Expected at least 2 loaded models (main + embeddings)");

        // sessions and clients should be present and numeric.
        Assert.Equal(JsonValueKind.Number, root.GetProperty("sessions").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("clients").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("uptime_sec").ValueKind);

        // uptime should be non-negative.
        Assert.True(root.GetProperty("uptime_sec").GetInt32() >= 0, "uptime_sec should be >= 0");
     }

    [Fact]
    public async Task Health_Reflects_Live_Session_Count()
     {
        // Baseline health.
        using var before = await _fixture.GetAsClientAsync("/eca/health");
        var beforeBody = await before.Content.ReadAsStringAsync();
        var beforeSessions = JsonDocument.Parse(beforeBody).RootElement.GetProperty("sessions").GetInt32();

         // Create a session.
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
         {
            using var after = await _fixture.GetAsClientAsync("/eca/health");
            var afterBody = await after.Content.ReadAsStringAsync();
            var afterSessions = JsonDocument.Parse(afterBody).RootElement.GetProperty("sessions").GetInt32();
            Assert.Equal(beforeSessions + 1, afterSessions);
         }
        finally
         {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
         }
     }
}
