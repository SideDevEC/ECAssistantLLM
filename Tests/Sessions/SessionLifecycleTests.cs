using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Sessions;

/// <summary>
/// Tests for session lifecycle: create, status, destroy, and validation.
/// </summary>
[Collection("Server")]
public class SessionLifecycleTests
{
    private readonly TestServerFixture _fixture;

    public SessionLifecycleTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateSession_With_Client_And_SessionId_Returns_200()
    {
        var clientId = await _fixture.RegisterClientAsync("session-creator");
        var sessionId = "sess-" + Guid.NewGuid().ToString("N")[..8];

        var resp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sessionId, model_id = (string?)null }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(sessionId, doc.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(clientId, doc.RootElement.GetProperty("client_id").GetString());

        // Cleanup
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
    }

    [Fact]
    public async Task CreateSession_Response_Contains_All_Fields()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            var resp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal(sessionId, root.GetProperty("session_id").GetString());
            Assert.Equal(clientId, root.GetProperty("client_id").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("model_id").GetString()));
            Assert.Equal(JsonValueKind.Number, root.GetProperty("context_size").ValueKind);
            Assert.Equal(JsonValueKind.Number, root.GetProperty("estimated_vram_mb").ValueKind);
            Assert.True(root.GetProperty("estimated_vram_mb").GetDouble() > 0);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task CreateSession_Without_Client_Header_Defaults_To_Default_Client()
    {
        // Server defaults X-Client-Id to "default" when header is missing.
        var sessionId = "no-header-" + Guid.NewGuid().ToString("N")[..8];
        using var resp = await _fixture.PostJsonAsync("/eca/sessions",
             new { session_id = sessionId });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(sessionId, doc.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("default", doc.RootElement.GetProperty("client_id").GetString());

        // Cleanup
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", "default");
    }

    [Fact]
    public async Task CreateSession_With_Missing_Session_Id_Returns_400()
    {
        var clientId = await _fixture.RegisterClientAsync("missing-sid");

        using var resp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { model_id = (string?)null }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateSession_With_Duplicate_Session_Id_Returns_400()
    {
        var clientId = await _fixture.RegisterClientAsync("dup-session");
        var sessionId = "dup-" + Guid.NewGuid().ToString("N")[..8];

        var first = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sessionId }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        first.Dispose();

        using var second = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sessionId }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_error",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());

        // Cleanup the first session.
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
    }

    [Fact]
    public async Task SessionStatus_With_Existing_Session_Returns_Details()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            var resp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(sessionId, doc.RootElement.GetProperty("session_id").GetString());
            Assert.Equal(clientId, doc.RootElement.GetProperty("client_id").GetString());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task SessionStatus_For_Nonexistent_Session_Returns_404()
    {
        var clientId = await _fixture.RegisterClientAsync("status-missing");

        using var resp = await _fixture.GetAsClientAsync(
              $"/eca/sessions/does-not-exist-{Guid.NewGuid():N}/status", clientId);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task DestroySession_With_Existing_Session_Returns_200()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();

        using var resp = await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DestroySession_For_Nonexistent_Returns_200_With_Ok_False()
    {
        var clientId = await _fixture.RegisterClientAsync("destroy-missing");

        using var resp = await _fixture.DeleteAsClientAsync(
              $"/eca/sessions/never-existed-{Guid.NewGuid():N}", clientId);

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task CreateSession_With_Unknown_Model_Id_Returns_400()
    {
        var clientId = await _fixture.RegisterClientAsync("bad-model");
        var sessionId = "badmodel-" + Guid.NewGuid().ToString("N")[..8];

        using var resp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sessionId, model_id = "no-such-model" }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_error",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }
}