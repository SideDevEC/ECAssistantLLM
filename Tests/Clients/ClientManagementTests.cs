using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Clients;

/// <summary>
/// Tests for client registration, heartbeat and disconnect
/// (POST /eca/clients, POST /eca/clients/{id}/heartbeat, DELETE /eca/clients/{id}).
/// </summary>
[Collection("Server")]
public class ClientManagementTests
{
    private readonly TestServerFixture _fixture;

    public ClientManagementTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RegisterClient_With_Valid_Name_Returns_32_Char_Hex_Id()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/clients",
             new { client_name = "alpha", version = "1.0.0" });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var clientId = doc.RootElement.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrEmpty(clientId));
        Assert.Equal(32, clientId!.Length);
        Assert.All(clientId, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public async Task RegisterClient_Without_Client_Name_Returns_400()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/clients",
             new { version = "1.0.0" }); // no client_name

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task RegisterClient_With_Empty_Body_Returns_400()
    {
        using var resp = await _fixture.PostRawAsync("/eca/clients", "");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RegisterClient_With_Whitespace_Name_Returns_400()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/clients",
             new { client_name = "    " });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Heartbeat_With_Valid_Client_Returns_Ok_True()
    {
        var clientId = await _fixture.RegisterClientAsync("heartbeat-client");

        using var resp = await _fixture.PostJsonAsClientAsync(
             $"/eca/clients/{clientId}/heartbeat", new { active_sessions = 1 }, "default");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_With_Unknown_Client_Returns_404()
    {
        using var resp = await _fixture.PostJsonAsClientAsync(
             "/eca/clients/00000000000000000000000000000000/heartbeat",
             new { active_sessions = 0 }, "default");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("client_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Delete_With_Valid_Client_Returns_200()
    {
        var clientId = await _fixture.RegisterClientAsync("to-be-removed");

        using var resp = await _fixture.DeleteAsClientAsync($"/eca/clients/{clientId}");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Delete_With_Unknown_Client_Returns_404()
    {
        using var resp = await _fixture.DeleteAsClientAsync(
             "/eca/clients/00000000000000000000000000000001");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("client_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Registering_Same_Name_Twice_Produces_Two_Different_Ids()
    {
        var id1 = await _fixture.RegisterClientAsync("duplicate-name");
        var id2 = await _fixture.RegisterClientAsync("duplicate-name");

        Assert.NotEqual(id1, id2);
        Assert.Equal(32, id1.Length);
        Assert.Equal(32, id2.Length);
    }
}