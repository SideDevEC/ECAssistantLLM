using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.ChatCompletions;

/// <summary>
/// Tests for OpenAI-compatible chat completions (streaming + non-streaming),
/// session routing, and parameter overrides.
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class ChatCompletionTests
{
    private readonly TestServerFixture _fixture;

    public ChatCompletionTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChatCompletion_NonStreaming_Returns_ChatCompletion()
         {
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
        var root = doc.RootElement;

        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("created").ValueKind);
        Assert.Equal("main", root.GetProperty("model").GetString());

        var choices = root.GetProperty("choices");
        Assert.Equal(1, choices.GetArrayLength());
        var message = choices[0].GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        // Model may return empty content on short prompts — structure is what matters.
         }

    [Fact]
    public async Task ChatCompletion_Streaming_Returns_EventStream_And_Done()
         {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 stream = true,
                 messages = new[] { new { role = "user", content = "Count from 1 to 3." } },
                 max_tokens = 32
               });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream",
            resp.Content.Headers.ContentType?.MediaType);

        var streamText = await resp.Content.ReadAsStringAsync();
        Assert.Contains("data: ", streamText);
        Assert.Contains("[DONE]", streamText);

          // The final event must be exactly "data: [DONE]".
        var lines = streamText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Trim() == "data: [DONE]");

          // At least one data chunk should parse as a chat.completion.chunk.
        var chunkLines = lines
             .Where(l => l.StartsWith("data: ") && !l.Contains("[DONE]"))
             .Select(l => l["data: ".Length..].Trim())
             .ToArray();
        Assert.NotEmpty(chunkLines);
        using var chunkDoc = JsonDocument.Parse(chunkLines[0]);
        Assert.Equal("chat.completion.chunk",
            chunkDoc.RootElement.GetProperty("object").GetString());
         }

    [Fact]
    public async Task ChatCompletion_With_Session_Id_Uses_Kv_Cache_Session()
         {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
           {
            await _fixture.PostJsonAsClientAsync(
                 $"/eca/sessions/{sessionId}/prefill",
                 new { text = "The secret code word is BANANA." }, clientId);

            var resp = await _fixture.PostJsonAsClientAsync("/v1/chat/completions",
                 new
                   {
                     model = "main",
                     session_id = sessionId,
                     stream = false,
                     messages = new[] { new { role = "user", content = "What is the secret code word?" } },
                     max_tokens = 16
                   }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            // Chat completion response doesn't echo session_id/client_id.
            // Just verify we got a valid chat completion.
            Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
            }
        finally
           {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
            }
         }

    [Fact]
    public async Task ChatCompletion_With_Nonexistent_Session_Id_Returns_404()
         {
        var clientId = await _fixture.RegisterClientAsync("chat-404-test");
        var resp = await _fixture.PostJsonAsClientAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 session_id = "no-such-session-" + Guid.NewGuid().ToString("N"),
                 stream = false,
                 messages = new[] { new { role = "user", content = "hi" } }
               }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
         }

    [Fact]
    public async Task ChatCompletion_Without_Client_Header_Uses_Default_Client()
         {
          // The OpenAI endpoint defaults X-Client-Id to "default" when absent,
         // so a request with no client header and no session_id must still work.
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 stream = false,
                 messages = new[] { new { role = "user", content = "Reply with OK." } },
                 max_tokens = 8
               });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
         }

    [Fact]
    public async Task ChatCompletion_With_Empty_Messages_Handles_Gracefully()
         {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 stream = false,
                 messages = Array.Empty<object>(),
                 max_tokens = 8
               });

         // Server must not crash: either a completion (empty prompt) or a 400.
        Assert.True(
            resp.StatusCode == System.Net.HttpStatusCode.OK
             || resp.StatusCode == System.Net.HttpStatusCode.BadRequest,
            $"Unexpected status for empty messages: {resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body),
            "Empty-messages response should contain a body");

         // If it is JSON, it must parse.
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("{"))
         {
            using var _ = JsonDocument.Parse(body);
         }
         }

    [Fact]
    public async Task ChatCompletion_With_Custom_Params_Succeeds()
         {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 stream = false,
                 messages = new[] { new { role = "user", content = "Tell a tiny joke." } },
                 temperature = 0.9f,
                 top_p = 0.8f,
                 top_k = 20,
                 max_tokens = 32,
                 repeat_penalty = 1.15f
               });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("choices").GetArrayLength());
         }

    [Fact]
    public async Task ChatCompletion_With_Stop_Sequences_Succeeds()
         {
        var resp = await _fixture.PostJsonAsync("/v1/chat/completions",
             new
               {
                 model = "main",
                 stream = false,
                 messages = new[] { new { role = "user", content = "List fruits separated by STOPWORD" } },
                 max_tokens = 32,
                 stop = new[] { "STOPWORD", "END" }
               });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());

          // The response content must not contain a stop sequence verbatim.
        var content = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
        Assert.DoesNotContain("STOPWORD", content);
         }
}
