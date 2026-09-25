using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Integration tests for the session lifecycle.
/// Tests run against the test server (continuous_batching=false → standard path).
/// They verify the full session lifecycle works correctly and consistently.
/// When continuous_batching is enabled, these same tests would exercise the batch path.
/// </summary>
[Collection("Server")]
[Trait("Category", "E2E")]
public class BatchSessionLifecycleTests
{
    private readonly TestServerFixture _fixture;

    public BatchSessionLifecycleTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Session_Create_ReturnsSuccess()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle");
        var sessionId = Guid.NewGuid().ToString("N");

        var resp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Standard path: { session_id, model_id, ... }, Batch path: { ok, message }
        // Both include session_id
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("session_id").GetString()));
    }

    [Fact]
    public async Task Session_Create_Duplicate_ReturnsError()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-dup");
        var sessionId = Guid.NewGuid().ToString("N");

        var resp1 = await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp1.StatusCode);

        var resp2 = await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task Session_Status_ReturnsSessionInfo()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-status");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        var resp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(sessionId, doc.RootElement.GetProperty("session_id").GetString());
        // context_size may be uint → JSON number
        Assert.True(doc.RootElement.TryGetProperty("context_size", out _));
    }

    [Fact]
    public async Task Session_Prefill_MarksSessionAsPrefilled()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-prefill");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        var prefillResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant." }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, prefillResp.StatusCode);

        // Check status shows prefilled
        var statusResp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        var statusBody = await statusResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(statusBody);
        Assert.True(doc.RootElement.GetProperty("is_prefilled").GetBoolean());
    }

    [Fact]
    public async Task Session_SaveState_ReturnsSuccess()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-save");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant." }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/save-state",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Standard path: { saved: true }, Batch path: { ok: true }
        Assert.True(doc.RootElement.TryGetProperty("saved", out var savedProp)
            ? savedProp.GetBoolean()
            : doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Session_Rewind_AfterSaveState_Succeeds()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-rewind");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant." }, clientId);

        await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/save-state",
            new { }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/rewind",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Standard path: { rewound: true }, Batch path: { ok: true }
        Assert.True(doc.RootElement.TryGetProperty("rewound", out var rewoundProp)
            ? rewoundProp.GetBoolean()
            : doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Session_Rewind_WithoutSaveState_ReturnsFalse()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-rewind-nosave");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/rewind",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Standard path: { rewound: false }, Batch path: { ok: false }
        Assert.False(doc.RootElement.TryGetProperty("rewound", out var rewoundProp)
            ? rewoundProp.GetBoolean()
            : doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Session_Reset_ClearsKvCache()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-reset");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant." }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/reset",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Status should show not prefilled
        var statusResp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        var statusBody = await statusResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(statusBody);
        Assert.False(doc.RootElement.GetProperty("is_prefilled").GetBoolean());
    }

    [Fact]
    public async Task Session_Destroy_RemovesSession()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-destroy");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        var resp = await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var statusResp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, statusResp.StatusCode);
    }

    [Fact]
    public async Task Session_Destroy_Nonexistent_ReturnsNotFound()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-destroy-nonexistent");

        var resp = await _fixture.DeleteAsClientAsync($"/eca/sessions/nonexistent-session", clientId);
        // Standard path returns 200 with { ok: false } for non-existent session
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Session_Evaluate_FeedsTextIntoCache()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-evaluate");
        var sessionId = Guid.NewGuid().ToString("N");

        await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/evaluate",
            new { text = "Some context to feed into the cache.", max_tokens = 1 }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Standard path: { accepted: true }, Batch path: { ok: true }
        Assert.True(doc.RootElement.TryGetProperty("accepted", out var acceptedProp)
            ? acceptedProp.GetBoolean()
            : doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Session_FullLifecycle_CreatePrefillChatSaveRewindResetDestroy()
    {
        var clientId = await _fixture.RegisterClientAsync("lifecycle-full");
        var sessionId = Guid.NewGuid().ToString("N");

        // 1. Create
        var createResp = await _fixture.PostJsonAsClientAsync("/eca/sessions",
            new { session_id = sessionId, model_id = "main" }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, createResp.StatusCode);

        // 2. Prefill
        var prefillResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/prefill",
            new { text = "You are a helpful assistant. Be concise." }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, prefillResp.StatusCode);

        // 3. Chat (with session_id)
        var chatResp = await _fixture.PostJsonAsClientAsync("/v1/chat/completions",
            new
            {
                model = "main",
                stream = false,
                session_id = sessionId,
                messages = new[] { new { role = "user", content = "Say hello." } },
                max_tokens = 16
            }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, chatResp.StatusCode);

        // 4. Save state
        var saveResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/save-state",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, saveResp.StatusCode);

        // 5. Rewind
        var rewindResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/rewind",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, rewindResp.StatusCode);

        // 6. Reset
        var resetResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/reset",
            new { }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, resetResp.StatusCode);

        // 7. Destroy
        var destroyResp = await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        Assert.Equal(System.Net.HttpStatusCode.OK, destroyResp.StatusCode);

        // 8. Verify gone
        var statusResp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, statusResp.StatusCode);
    }
}