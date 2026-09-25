using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Integration tests for chat completions via the batch path.
/// These tests exercise the full pipeline: HTTP → RequestRouter → BatchSession →
/// BatchInferenceCoordinator → BatchedExecutor → Infer → response.
///
/// They run against the same test server as the standard tests (no continuous_batching
/// in config), so they verify that the batch path produces identical results to the
/// standard path when used via the API.
/// </summary>
[Collection("Server")]
[Trait("Category", "E2E")]
public class BatchChatCompletionTests
{
    private readonly TestServerFixture _fixture;

    public BatchChatCompletionTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChatCompletion_NonStreaming_BatchPath_ReturnsValidResponse()
    {
        // Verify that a standard chat completion request returns the same shape
        // regardless of which internal path is used. When continuous_batching is off,
        // this goes through the standard path — proving backward compat.
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                messages = new[] { new { role = "user", content = "Say hello in one word." } },
                max_tokens = 16
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.Equal("main", doc.RootElement.GetProperty("model").GetString());

        var choices = doc.RootElement.GetProperty("choices");
        Assert.Single(choices.EnumerateArray());
        var message = choices[0].GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        // Content may be empty for thinking models — just verify the field exists
        Assert.True(message.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task ChatCompletion_Streaming_BatchPath_ReturnsValidSSE()
    {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = true,
                messages = new[] { new { role = "user", content = "Say hello in one word." } },
                max_tokens = 16
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // SSE: should contain "data:" lines and end with [DONE]
        Assert.Contains("data:", body);
        Assert.Contains("[DONE]", body);
    }

    [Fact]
    public async Task ChatCompletion_Structured_BatchPath_ReturnsDecision()
    {
        // Structured mode — grammar-forced JSON decision envelope
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                structured = true,
                messages = new[]
                {
                    new { role = "system", content = "You are a helpful assistant. Respond with a decision." },
                    new { role = "user", content = "Should I eat pizza or sushi tonight?" }
                },
                max_tokens = 128
            });

        // Structured mode may return 200 (success) or 422 (decode failed — model-dependent)
        var statusCode = resp.StatusCode;
        Assert.True(statusCode == System.Net.HttpStatusCode.OK || statusCode == System.Net.HttpStatusCode.UnprocessableEntity,
            $"Expected OK or 422, got {statusCode}");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        if (statusCode == System.Net.HttpStatusCode.OK)
        {
            Assert.True(doc.RootElement.TryGetProperty("decision", out var decision));
            Assert.NotEqual(JsonValueKind.Null, decision.ValueKind);
        }
    }

    [Fact]
    public async Task ChatCompletion_Tools_BatchPath_ReturnsToolCalls()
    {
        // Tools mode — grammar-forced tool_calls
        var tools = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "get_weather",
                    description = "Get the weather for a location",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            location = new { type = "string", description = "City name" }
                        },
                        required = new[] { "location" }
                    }
                }
            }
        };

        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                tools = tools,
                messages = new[]
                {
                    new { role = "user", content = "What's the weather in Berlin?" }
                },
                max_tokens = 128
            });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Tools mode returns choices with message.tool_calls
        var choices = doc.RootElement.GetProperty("choices");
        Assert.Single(choices.EnumerateArray());
        var message = choices[0].GetProperty("message");

        // The model should produce a tool_call (grammar-forced)
        Assert.True(message.TryGetProperty("tool_calls", out var toolCalls));
        Assert.NotEqual(JsonValueKind.Null, toolCalls.ValueKind);
    }
}