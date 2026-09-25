using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Integration tests for backward compatibility — verifies that the standard
/// (non-batch) path still works identically when continuous_batching is NOT enabled.
/// These tests run against the same test server (which has continuous_batching=false
/// in config), proving zero regression.
/// </summary>
[Collection("Server")]
[Trait("Category", "E2E")]
public class BatchBackwardCompatTests
{
    private readonly TestServerFixture _fixture;

    public BatchBackwardCompatTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StandardChat_NonStreaming_StillWorks()
    {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                messages = new[] { new { role = "user", content = "What is 2+2?" } },
                max_tokens = 16
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task StandardChat_Streaming_StillWorks()
    {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = true,
                messages = new[] { new { role = "user", content = "What is 2+2?" } },
                max_tokens = 16
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("data:", body);
        Assert.Contains("[DONE]", body);
    }

    [Fact]
    public async Task StandardSession_CreateAndDestroy_StillWorks()
    {
        var clientId = await _fixture.RegisterClientAsync("compat-session");
        var sessionId = Guid.NewGuid().ToString("N");

        var createResp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, createResp.StatusCode);

        var destroyResp = await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, destroyResp.StatusCode);
    }

    [Fact]
    public async Task StandardSession_PrefillAndChat_StillWorks()
    {
        var clientId = await _fixture.RegisterClientAsync("compat-prefill");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant." }, clientId);

        var chatResp = await _fixture.PostJsonAsClientAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                session_id = sessionId,
                messages = new[] { new { role = "user", content = "Say hi." } },
                max_tokens = 16
            }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.OK, chatResp.StatusCode);
    }

    [Fact]
    public async Task StandardStructured_StillWorks()
    {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                structured = true,
                messages = new[]
                {
                    new { role = "system", content = "Respond with a decision." },
                    new { role = "user", content = "Should I walk or drive?" }
                },
                max_tokens = 128
            });

        // Structured mode may return 200 or 422 (model-dependent)
        var statusCode = resp.StatusCode;
        Assert.True(statusCode == System.Net.HttpStatusCode.OK || statusCode == System.Net.HttpStatusCode.UnprocessableEntity,
            $"Expected OK or 422, got {statusCode}");
        if (statusCode == System.Net.HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("decision", out _));
        }
    }

    [Fact]
    public async Task StandardTools_StillWorks()
    {
        var tools = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "get_time",
                    description = "Get current time",
                    parameters = new { type = "object", properties = new { } }
                }
            }
        };

        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                tools = tools,
                messages = new[] { new { role = "user", content = "What time is it?" } },
                max_tokens = 64
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task StatelessChat_StillWorks()
    {
        // No session_id = stateless inference
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                messages = new[] { new { role = "user", content = "Say one word." } },
                max_tokens = 8
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }
}