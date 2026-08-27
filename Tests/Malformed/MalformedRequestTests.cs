using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Malformed;

/// <summary>
/// Tests for malformed requests — valid JSON but wrong types, missing fields,
/// or unexpected values. Server should handle gracefully (400 or tolerant), never 500.
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class MalformedRequestTests
{
    private readonly TestServerFixture _fixture;

    public MalformedRequestTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChatCompletion_Messages_As_String_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","messages":"not an array","max_tokens":8}""");

        // Should be 400 or a graceful error, not 500
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task ChatCompletion_Model_As_Number_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":123,"messages":[{"role":"user","content":"hi"}],"max_tokens":8}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task Completion_Prompt_As_Number_Returns_400()
    {
        using var resp = await _fixture.PostRawAsync("/v1/completions",
            """{"model":"main","prompt":123,"max_tokens":8}""");

        // Empty string deserialization → 400 (prompt is required)
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChatCompletion_Temperature_As_String_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","messages":[{"role":"user","content":"hi"}],"temperature":"hot","max_tokens":8}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task ChatCompletion_Negative_MaxTokens_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","messages":[{"role":"user","content":"hi"}],"max_tokens":-5}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task Embeddings_Input_As_Number_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/embeddings",
            """{"model":"embeddings","input":42}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task CreateSession_SessionId_As_Number_Returns_Gracefully()
    {
        var clientId = await _fixture.RegisterClientAsync("malformed-sid");

        using var resp = await _fixture.PostRawAsync("/eca/sessions",
            """{"session_id":123}""", clientId);

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task CreateSession_With_Extra_Unknown_Fields_Is_Ignored()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync(sessionId: "extra-" + Guid.NewGuid().ToString("N")[..8]);

        // Cleanup
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
    }

    [Fact]
    public async Task ChatCompletion_Stream_As_String_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","messages":[{"role":"user","content":"hi"}],"stream":"yes","max_tokens":8}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task ChatCompletion_Empty_Messages_Array_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","messages":[],"max_tokens":8}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task ChatCompletion_Missing_Messages_Field_Returns_Gracefully()
    {
        using var resp = await _fixture.PostRawAsync("/v1/chat/completions",
            """{"model":"main","max_tokens":8}""");

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}