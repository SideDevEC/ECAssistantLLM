using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.KvCache;

/// <summary>
/// Tests for KV-cache operations: prefill, save-state, rewind, reset, and the
/// full create → prefill → save → infer → rewind → reset → destroy lifecycle.
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class KvCacheTests
{
    private readonly TestServerFixture _fixture;

    public KvCacheTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Prefill_Returns_Prefilled_True_With_Tokens()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill",
                 new { text = "The quick brown fox jumps over the lazy dog." }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("prefilled").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("tokens").GetInt32() > 0);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Prefill_Twice_Is_Idempotent()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        var text = "Repeat after me: alpha bravo charlie delta.";
        try
        {
            using var first = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill", new { text }, clientId);
            var firstBody = await first.Content.ReadAsStringAsync();
            var firstTokens = JsonDocument.Parse(firstBody).RootElement.GetProperty("tokens").GetInt32();
            Assert.True(firstTokens > 0, "First prefill should report tokens > 0");

            using var second = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill", new { text }, clientId);
            var secondBody = await second.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(secondBody);

            Assert.True(doc.RootElement.GetProperty("prefilled").GetBoolean());
            // Second prefill returns the same token count (idempotent)
            Assert.Equal(firstTokens, doc.RootElement.GetProperty("tokens").GetInt32());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Prefill_Nonexistent_Session_Returns_404()
    {
        var clientId = await _fixture.RegisterClientAsync("prefill-missing");

        using var resp = await _fixture.PostJsonAsClientAsync(
               $"/eca/sessions/ghost-{Guid.NewGuid():N}/prefill",
             new { text = "hello" }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Prefill_Without_Client_Header_Returns_404_For_Nonexistent_Session()
    {
        // Server defaults X-Client-Id to "default" when missing.
        // Session "whatever" doesn't exist under "default", so 404.
        using var resp = await _fixture.PostJsonAsync(
              "/eca/sessions/whatever/prefill", new { text = "hi" });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Prefill_With_Empty_Text_Returns_400()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill", new { text = "" }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task SaveState_On_Prefilled_Session_Returns_Saved_True()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill",
                 new { text = "Context that will be saved to disk." }, clientId);

            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/save-state", new { }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("saved").GetBoolean());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Rewind_After_Save_Returns_Rewound_True()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill",
                 new { text = "First context block." }, clientId);

            var saveResp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/save-state", new { }, clientId);
            var saveBody = await saveResp.Content.ReadAsStringAsync();
            var saved = JsonDocument.Parse(saveBody).RootElement.GetProperty("saved").GetBoolean();
            Assert.True(saved);

            using var rewResp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/rewind",
                 new { state_id = "checkpoint" }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, rewResp.StatusCode);
            var rewBody = await rewResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(rewBody);
            Assert.True(doc.RootElement.GetProperty("rewound").GetBoolean());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Rewind_Without_Saved_State_Returns_Rewound_False()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/rewind", new { state_id = "checkpoint" }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("rewound").GetBoolean());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Reset_Session_Returns_Ok()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            using var resp = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/reset", new { }, clientId);

            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Reset_Nonexistent_Session_Returns_404()
    {
        var clientId = await _fixture.RegisterClientAsync("reset-missing");

        using var resp = await _fixture.PostJsonAsClientAsync(
               $"/eca/sessions/ghost-{Guid.NewGuid():N}/reset", new { }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_not_found",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Full_Lifecycle_Create_Prefill_Save_Infer_Rewind_Reset_Destroy()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            // 1. Prefill context.
            var pf = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/prefill",
                 new { text = "You are a helpful assistant. The topic is kv-cache." }, clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, pf.StatusCode);
            pf.Dispose();

            // 2. Save state.
            var sv = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/save-state", new { }, clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, sv.StatusCode);
            sv.Dispose();

            // 3. Inference against the session (uses the KV cache).
            var chat = await _fixture.PostJsonAsClientAsync("/v1/chat/completions",
                 new
                  {
                     model = "main",
                     session_id = sessionId,
                     stream = false,
                     messages = new[] { new { role = "user", content = "What is the topic?" } },
                     max_tokens = 32
                  }, clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, chat.StatusCode);
            chat.Dispose();

            // 4. Rewind to the saved checkpoint.
            var rw = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/rewind", new { state_id = "checkpoint" }, clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, rw.StatusCode);
            var rwBody = await rw.Content.ReadAsStringAsync();
            Assert.True(JsonDocument.Parse(rwBody).RootElement.GetProperty("rewound").GetBoolean());
            rw.Dispose();

            // 5. Reset the session.
            var rs = await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sessionId}/reset", new { }, clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, rs.StatusCode);
            rs.Dispose();
        }
        finally
        {
            // 6. Destroy the session.
            var del = await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
            Assert.Equal(System.Net.HttpStatusCode.OK, del.StatusCode);
            del.Dispose();
        }
    }
}