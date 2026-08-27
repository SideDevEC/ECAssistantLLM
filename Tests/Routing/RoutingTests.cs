using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Routing;

/// <summary>
/// Tests for client routing and session namespacing:
/// X-Client-Id defaulting for OpenAI endpoints, and per-client session isolation.
/// </summary>
[Collection("Server")]
public class RoutingTests
{
    private readonly TestServerFixture _fixture;

    public RoutingTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OpenAi_Endpoint_Defaults_Client_To_Default_When_Header_Missing()
        {
          // A chat completion with no X-Client-Id header and no session_id
         // must succeed, proving the router defaulted the client to "default".
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
                {
                 model = "main",
                 stream = false,
                 messages = new[] { new { role = "user", content = "Reply OK." } },
                 max_tokens = 8
                });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        }

    [Fact]
    public async Task Eca_Endpoint_Respects_X_Client_Id_Header()
        {
          // Create a session under a specific client.
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
          {
           // Status under the SAME client works.
          var same = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
          Assert.Equal(System.Net.HttpStatusCode.OK, same.StatusCode);
          same.Dispose();

           // Status under a DIFFERENT client fails (namespacing).
          var otherClient = await _fixture.RegisterClientAsync("routing-other");
          var other = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", otherClient);
          Assert.Equal(System.Net.HttpStatusCode.NotFound, other.StatusCode);
          other.Dispose();
          }
        finally
          {
          await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
          }
        }

    [Fact]
    public async Task Two_Clients_Can_Have_Sessions_With_Same_Session_Id()
        {
          // Register two distinct clients.
        var clientA = await _fixture.RegisterClientAsync("routing-a");
        var clientB = await _fixture.RegisterClientAsync("routing-b");
        var sharedId = "shared-" + Guid.NewGuid().ToString("N")[..8];

        // Both clients create a session with the same session_id.
        var createA = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sharedId }, clientA);
        Assert.Equal(System.Net.HttpStatusCode.OK, createA.StatusCode);
        createA.Dispose();

        var createB = await _fixture.PostJsonAsClientAsync("/eca/sessions",
             new { session_id = sharedId }, clientB);
        Assert.Equal(System.Net.HttpStatusCode.OK, createB.StatusCode);
        createB.Dispose();

        try
         {
          // Each client sees its own session.
          var statusA = await _fixture.GetAsClientAsync($"/eca/sessions/{sharedId}/status", clientA);
          Assert.Equal(System.Net.HttpStatusCode.OK, statusA.StatusCode);
          var bodyA = await statusA.Content.ReadAsStringAsync();
          Assert.Equal(clientA,
              JsonDocument.Parse(bodyA).RootElement.GetProperty("client_id").GetString());
          statusA.Dispose();

          var statusB = await _fixture.GetAsClientAsync($"/eca/sessions/{sharedId}/status", clientB);
          Assert.Equal(System.Net.HttpStatusCode.OK, statusB.StatusCode);
          var bodyB = await statusB.Content.ReadAsStringAsync();
          Assert.Equal(clientB,
              JsonDocument.Parse(bodyB).RootElement.GetProperty("client_id").GetString());
          statusB.Dispose();
         }
        finally
         {
          await _fixture.DeleteAsClientAsync($"/eca/sessions/{sharedId}", clientA);
          await _fixture.DeleteAsClientAsync($"/eca/sessions/{sharedId}", clientB);
         }
        }

    [Fact]
    public async Task Client_A_Cannot_Access_Client_B_Session()
        {
        var (clientIdA, sessionId) = await _fixture.CreateSessionAsync(clientName: "isolated-a");
        var clientB = await _fixture.RegisterClientAsync("isolated-b");

        try
         {
          // Client B tries to status / prefill Client A's session.
          var statusB = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientB);
          Assert.Equal(System.Net.HttpStatusCode.NotFound, statusB.StatusCode);
          var statusBody = await statusB.Content.ReadAsStringAsync();
          Assert.Equal("session_not_found",
              JsonDocument.Parse(statusBody).RootElement.GetProperty("error").GetProperty("type").GetString());
          statusB.Dispose();

          var prefillB = await _fixture.PostJsonAsClientAsync(
               $"/eca/sessions/{sessionId}/prefill", new { text = "snoop" }, clientB);
          Assert.Equal(System.Net.HttpStatusCode.NotFound, prefillB.StatusCode);
          prefillB.Dispose();

          var resetB = await _fixture.PostJsonAsClientAsync(
               $"/eca/sessions/{sessionId}/reset", new { }, clientB);
          Assert.Equal(System.Net.HttpStatusCode.NotFound, resetB.StatusCode);
          resetB.Dispose();
         }
        finally
         {
          await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientIdA);
         }
        }
}
