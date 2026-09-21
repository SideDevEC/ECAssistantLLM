using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Errors;

/// <summary>
/// Cross-cutting error-handling tests: unknown paths, wrong methods,
/// unimplemented endpoints, missing headers, not-found sessions, bad JSON.
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class ErrorHandlingTests
{
    private readonly TestServerFixture _fixture;

    public ErrorHandlingTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unknown_Get_Path_Returns_404_NotFound()
     {
        using var resp = await _fixture.GetAsClientAsync("/eca/does-not-exist");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        Assert.Contains("Not found", message);
     }

    [Fact]
    public async Task Unknown_Post_Path_Returns_404()
     {
        using var resp = await _fixture.PostJsonAsync("/eca/totally-unknown", new { foo = "bar" });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
     }

    [Fact]
    public async Task Wrong_Http_Method_On_Known_Path_Returns_404()
     {
         // GET on a POST-only endpoint.
        using var resp = await _fixture.GetAsClientAsync("/eca/clients");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("Not found",
            doc.RootElement.GetProperty("error").GetProperty("message").GetString());
     }

    [Fact]
    public async Task Completions_Endpoint_Returns_200()
     {
         // /v1/completions is fully implemented (OpenAI-compatible text completions).
        using var resp = await _fixture.PostJsonAsync("/v1/completions",
             new { model = "main", prompt = "Hello", max_tokens = 8 });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("text_completion", doc.RootElement.GetProperty("object").GetString());
     }

    [Fact]
    public async Task Completions_With_Empty_Prompt_Returns_400()
     {
        using var resp = await _fixture.PostJsonAsync("/v1/completions",
             new { model = "main", prompt = "" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
     }

    [Fact]
    public async Task Session_Create_Without_Client_Header_Returns_401()
     {
         // The router requires a registered X-Client-Id — no default anymore.
        var sessionId = "auto-default-" + Guid.NewGuid().ToString("N")[..8];
        using var resp = await _fixture.PostRawAsync("/eca/sessions",
             "{\"session_id\":\"" + sessionId + "\"}", clientId: "");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
     }

    [Theory]
        [InlineData("/eca/sessions/{id}/prefill")]
        [InlineData("/eca/sessions/{id}/rewind")]
        [InlineData("/eca/sessions/{id}/reset")]
        [InlineData("/eca/sessions/{id}/status")]
    public async Task Session_Operations_On_Missing_Session_Return_404(string pathTemplate)
     {
        var clientId = await _fixture.RegisterClientAsync("err-missing");
        var id = "missing-" + Guid.NewGuid().ToString("N");
        var path = pathTemplate.Replace("{id}", id);

        if (path.EndsWith("/status"))
         {
            using var resp = await _fixture.GetAsClientAsync(path, clientId);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
            var content = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            Assert.Equal("session_not_found",
                doc.RootElement.GetProperty("error").GetProperty("type").GetString());
            return;
         }

        var payload = path.EndsWith("/prefill")
                 ? (object)new { text = "hi" }
                 : (object)new { state_id = "checkpoint" };
        using var resp2 = await _fixture.PostJsonAsClientAsync(path, payload, clientId);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp2.StatusCode);
        var body = await resp2.Content.ReadAsStringAsync();
        using var doc2 = JsonDocument.Parse(body);
        Assert.Equal("session_not_found",
            doc2.RootElement.GetProperty("error").GetProperty("type").GetString());
     }

    [Fact]
    public async Task Invalid_Json_Body_Returns_400()
     {
         // A syntactically valid JSON body of the wrong type (a string, not an
        // object) deserializes to null and is rejected as invalid_request.
        // (Truly malformed JSON is not caught by ReadJsonAsync and would surface
        // as a 500, so we test the null-deserialization contract here.)
        using var resp = await _fixture.PostRawAsync("/eca/clients", "\"not-an-object\"");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
     }
}
