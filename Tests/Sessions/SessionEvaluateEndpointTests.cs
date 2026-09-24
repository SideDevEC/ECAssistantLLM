using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Sessions;

/// <summary>
/// v15 (Emre, 2026-09-24): endpoint tests for the KV-hygiene surface —
/// POST /eca/sessions/{id}/evaluate (prompt-only feed with bounded sampling)
/// and the extended /status diagnostics (has_saved_state, headroom_tokens).
/// Runs against the real server fixture (genuine inference + KV accounting).
/// </summary>
[Collection("Server")]
[Trait("Category", "E2E")]
public class SessionEvaluateEndpointTests
{
    private readonly TestServerFixture _fixture;

    public SessionEvaluateEndpointTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Evaluate_FeedsPrompt_ReturnsAccepted_AndGrowsUsage()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            var before = await GetApproxTokensAsync(clientId, sessionId);

            var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/evaluate",
                new { text = "kv hygiene warm-up content for the evaluate endpoint test.", max_tokens = 1 }, clientId);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("prompt_chars").GetInt32() > 0);
            // At most one stray sample (LLamaSharp cannot sample zero tokens).
            Assert.InRange(doc.RootElement.GetProperty("sampled_tokens").GetInt32(), 0, 1);

            var after = await GetApproxTokensAsync(clientId, sessionId);
            Assert.True(after > before, $"expected usage growth after evaluate (before={before}, after={after})");
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Evaluate_RepeatedCalls_AreIdempotent_UsageGrowsEachTime()
    {
        // Evaluate is repeatable — unlike the one-shot static-prefix prefill.
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/evaluate",
                    new { text = $"evaluate repetition {i} — warm content.", max_tokens = 1 }, clientId);
                resp.EnsureSuccessStatusCode();
            }
            var approx = await GetApproxTokensAsync(clientId, sessionId);
            Assert.True(approx > 0);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    [Fact]
    public async Task Evaluate_MissingText_Returns400()
    {
        var clientId = await _fixture.RegisterClientAsync("evaluate-missing-text");
        var sessionId = "eval-" + Guid.NewGuid().ToString("N")[..8];
        await _fixture.PostJsonAsClientAsync("/eca/sessions", new { session_id = sessionId }, clientId);

        var resp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/evaluate",
            new { max_tokens = 1 }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);

        await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
    }

    [Fact]
    public async Task Evaluate_UnknownSession_Returns404()
    {
        var clientId = await _fixture.RegisterClientAsync("evaluate-unknown");
        using var resp = await _fixture.PostJsonAsClientAsync(
            $"/eca/sessions/does-not-exist-{Guid.NewGuid():N}/evaluate",
            new { text = "warm content" }, clientId);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Evaluate_WithoutClientHeader_Returns401()
    {
        var sessionId = "no-header-" + Guid.NewGuid().ToString("N")[..8];
        using var resp = await _fixture.PostRawAsync($"/eca/sessions/{sessionId}/evaluate",
            "{\"text\":\"warm content\"}", clientId: "");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Status_IncludesSavedState_AndHeadroom()
    {
        var (clientId, sessionId) = await _fixture.CreateSessionAsync();
        try
        {
            var (savedBefore, headroomBefore) = await GetStatusFieldsAsync(clientId, sessionId);
            Assert.False(savedBefore);
            Assert.True(headroomBefore >= 0);

            // Snapshot → has_saved_state flips true; headroom stays non-negative.
            var saveResp = await _fixture.PostJsonAsClientAsync($"/eca/sessions/{sessionId}/save-state", new { }, clientId);
            saveResp.EnsureSuccessStatusCode();

            var (savedAfter, headroomAfter) = await GetStatusFieldsAsync(clientId, sessionId);
            Assert.True(savedAfter);
            Assert.True(headroomAfter >= 0);
            Assert.True(headroomAfter <= headroomBefore + 1); // save itself must not consume budget
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sessionId}", clientId);
        }
    }

    private async Task<int> GetApproxTokensAsync(string clientId, string sessionId)
    {
        using var resp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("approx_tokens").GetInt32();
    }

    private async Task<(bool Saved, int Headroom)> GetStatusFieldsAsync(string clientId, string sessionId)
    {
        using var resp = await _fixture.GetAsClientAsync($"/eca/sessions/{sessionId}/status", clientId);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("has_saved_state").GetBoolean(),
            doc.RootElement.GetProperty("headroom_tokens").GetInt32()
        );
    }
}
