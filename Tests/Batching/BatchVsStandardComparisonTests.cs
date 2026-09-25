using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Batching;

/// <summary>
/// Collection definition for the batch test server fixture.
/// </summary>
[CollectionDefinition("BatchServer")]
public class BatchServerCollection : ICollectionFixture<BatchServerFixture>
{
}

/// <summary>
/// Comparison tests — run the SAME requests against BOTH the standard server (port 8421)
/// and the batch server (port 8422, continuous_batching=true). Assert that both return
/// the same status codes and response shapes.
/// </summary>
[Collection("BatchServer")]
[Trait("Category", "E2E")]
public class BatchVsStandardComparisonTests
{
    private const string StdUrl = "http://localhost:8421";
    private readonly BatchServerFixture _batchFixture;
    private string BatchUrl => _batchFixture.BaseUrl;

    public BatchVsStandardComparisonTests(BatchServerFixture batchFixture) => _batchFixture = batchFixture;

    [Fact]
    public async Task Chat_NonStreaming_BothPaths_SameShape()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-chat-ns");
        var req = new { model = "main", stream = false, messages = new[] { new { role = "user", content = "Say hello." } }, max_tokens = 16 };

        var (stdResp, batchResp) = await PostBoth("/v1/chat/completions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);
        using var stdDoc = JsonDocument.Parse(stdBody);
        using var batchDoc = JsonDocument.Parse(batchBody);

        Assert.Equal("chat.completion", stdDoc.RootElement.GetProperty("object").GetString());
        Assert.Equal("chat.completion", batchDoc.RootElement.GetProperty("object").GetString());
        Assert.Equal("main", stdDoc.RootElement.GetProperty("model").GetString());
        Assert.Equal("main", batchDoc.RootElement.GetProperty("model").GetString());
        Assert.Single(stdDoc.RootElement.GetProperty("choices").EnumerateArray());
        Assert.Single(batchDoc.RootElement.GetProperty("choices").EnumerateArray());
        Assert.Equal("assistant", stdDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("assistant", batchDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Chat_Streaming_BothPaths_SameFormat()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-chat-stream");
        var req = new { model = "main", stream = true, messages = new[] { new { role = "user", content = "Say hello." } }, max_tokens = 16 };

        var (stdResp, batchResp) = await PostBoth("/v1/chat/completions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);

        Assert.Contains("data:", stdBody);
        Assert.Contains("data:", batchBody);
        Assert.Contains("[DONE]", stdBody);
        Assert.Contains("[DONE]", batchBody);
    }

    [Fact]
    public async Task Chat_Structured_BothPaths_SameStatus()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-struct");
        var req = new { model = "main", stream = false, structured = true, messages = new[] { new { role = "system", content = "Respond with a decision." }, new { role = "user", content = "Pizza or sushi?" } }, max_tokens = 128 };

        var (stdResp, batchResp) = await PostBoth("/v1/chat/completions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);
        using var stdDoc = JsonDocument.Parse(stdBody);
        using var batchDoc = JsonDocument.Parse(batchBody);
        Assert.Equal(stdDoc.RootElement.TryGetProperty("decision", out _), batchDoc.RootElement.TryGetProperty("decision", out _));
    }

    [Fact]
    public async Task Chat_Tools_BothPaths_SameShape()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-tools");
        var tools = new[] { new { type = "function", function = new { name = "get_weather", description = "Get weather", parameters = new { type = "object", properties = new { location = new { type = "string", description = "City" } }, required = new[] { "location" } } } } };
        var req = new { model = "main", stream = false, tools = tools, messages = new[] { new { role = "user", content = "Weather in Berlin?" } }, max_tokens = 128 };

        var (stdResp, batchResp) = await PostBoth("/v1/chat/completions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);
        using var stdDoc = JsonDocument.Parse(stdBody);
        using var batchDoc = JsonDocument.Parse(batchBody);

        var stdHas = stdDoc.RootElement.GetProperty("choices")[0].GetProperty("message").TryGetProperty("tool_calls", out _);
        var batchHas = batchDoc.RootElement.GetProperty("choices")[0].GetProperty("message").TryGetProperty("tool_calls", out _);
        Assert.Equal(stdHas, batchHas);
    }

    [Fact]
    public async Task Chat_Stateless_BothPaths_SameShape()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-stateless");
        var req = new { model = "main", stream = false, messages = new[] { new { role = "user", content = "What is 2+2?" } }, max_tokens = 16 };

        var (stdResp, batchResp) = await PostBoth("/v1/chat/completions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);
        using var stdDoc = JsonDocument.Parse(stdBody);
        using var batchDoc = JsonDocument.Parse(batchBody);
        Assert.Equal(stdDoc.RootElement.GetProperty("object").GetString(), batchDoc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task Session_Create_BothPaths_SameShape()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-session-create");
        var sessionId = Guid.NewGuid().ToString("N");
        var req = new { session_id = sessionId, model_id = "main" };

        var (stdResp, batchResp) = await PostBoth("/eca/sessions", req, stdCid, batchCid);

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);
        var (stdBody, batchBody) = await ReadBoth(stdResp, batchResp);
        using var stdDoc = JsonDocument.Parse(stdBody);
        using var batchDoc = JsonDocument.Parse(batchBody);
        Assert.False(string.IsNullOrEmpty(stdDoc.RootElement.GetProperty("session_id").GetString()));
        Assert.False(string.IsNullOrEmpty(batchDoc.RootElement.GetProperty("session_id").GetString()));
    }

    [Fact]
    public async Task Session_Prefill_BothPaths_SameResult()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-prefill");
        var stdSid = Guid.NewGuid().ToString("N");
        var batchSid = Guid.NewGuid().ToString("N");

        // Create sessions
        await PostOn(StdUrl, "/eca/sessions", new { session_id = stdSid, model_id = "main" }, stdCid);
        await PostOn(BatchUrl, "/eca/sessions", new { session_id = batchSid, model_id = "main" }, batchCid);

        // Prefill
        var (stdResp, batchResp) = await PostBoth($"/eca/sessions/{stdSid}/prefill", new { text = "You are a helpful assistant." }, stdCid, batchCid, batchPathOverride: $"/eca/sessions/{batchSid}/prefill");

        Assert.Equal(stdResp.StatusCode, batchResp.StatusCode);

        // Check status — both prefilled
        var (stdStatus, batchStatus) = await GetBoth($"/eca/sessions/{stdSid}/status", stdCid, $"/eca/sessions/{batchSid}/status", batchCid);
        var (stdStatusBody, batchStatusBody) = await ReadBoth(stdStatus, batchStatus);
        using var stdDoc = JsonDocument.Parse(stdStatusBody);
        using var batchDoc = JsonDocument.Parse(batchStatusBody);
        Assert.True(stdDoc.RootElement.GetProperty("is_prefilled").GetBoolean());
        Assert.True(batchDoc.RootElement.GetProperty("is_prefilled").GetBoolean());
    }

    [Fact]
    public async Task Session_FullLifecycle_BothPaths_Identical()
    {
        var (stdCid, batchCid) = await RegisterBoth("cmp-full");
        var stdSid = Guid.NewGuid().ToString("N");
        var batchSid = Guid.NewGuid().ToString("N");

        // Create
        var (stdCreate, batchCreate) = await PostBoth("/eca/sessions", new { session_id = stdSid, model_id = "main" }, stdCid, batchCid, batchPathOverride: "/eca/sessions", batchBodyOverride: new { session_id = batchSid, model_id = "main" });
        Assert.Equal(stdCreate.StatusCode, batchCreate.StatusCode);

        // Prefill
        var (stdPrefill, batchPrefill) = await PostBoth($"/eca/sessions/{stdSid}/prefill", new { text = "You are a helpful assistant." }, stdCid, batchCid, batchPathOverride: $"/eca/sessions/{batchSid}/prefill");
        Assert.Equal(stdPrefill.StatusCode, batchPrefill.StatusCode);

        // Save
        var (stdSave, batchSave) = await PostBoth($"/eca/sessions/{stdSid}/save-state", new { }, stdCid, batchCid, batchPathOverride: $"/eca/sessions/{batchSid}/save-state");
        Assert.Equal(stdSave.StatusCode, batchSave.StatusCode);

        // Rewind
        var (stdRewind, batchRewind) = await PostBoth($"/eca/sessions/{stdSid}/rewind", new { }, stdCid, batchCid, batchPathOverride: $"/eca/sessions/{batchSid}/rewind");
        Assert.Equal(stdRewind.StatusCode, batchRewind.StatusCode);

        // Reset
        var (stdReset, batchReset) = await PostBoth($"/eca/sessions/{stdSid}/reset", new { }, stdCid, batchCid, batchPathOverride: $"/eca/sessions/{batchSid}/reset");
        Assert.Equal(stdReset.StatusCode, batchReset.StatusCode);

        // Destroy
        var (stdDestroy, batchDestroy) = await DeleteBoth($"/eca/sessions/{stdSid}", stdCid, $"/eca/sessions/{batchSid}", batchCid);
        Assert.Equal(stdDestroy.StatusCode, batchDestroy.StatusCode);

        // Status — both 404
        var (stdStatus, batchStatus) = await GetBoth($"/eca/sessions/{stdSid}/status", stdCid, $"/eca/sessions/{batchSid}/status", batchCid);
        Assert.Equal(stdStatus.StatusCode, batchStatus.StatusCode);
    }

    // ── Combined helpers ──

    private async Task<(string stdCid, string batchCid)> RegisterBoth(string name)
    {
        var stdCid = await RegisterOn(StdUrl, name + "-std");
        var batchCid = await RegisterOn(BatchUrl, name + "-batch");
        return (stdCid, batchCid);
    }

    private async Task<(HttpResponseMessage std, HttpResponseMessage batch)> PostBoth(
        string path, object stdBody, string stdCid, string batchCid,
        string? batchPathOverride = null, object? batchBodyOverride = null)
    {
        var stdResp = await PostOn(StdUrl, path, stdBody, stdCid);
        var batchResp = await PostOn(BatchUrl, batchPathOverride ?? path, batchBodyOverride ?? stdBody, batchCid);
        return (stdResp, batchResp);
    }

    private async Task<(HttpResponseMessage std, HttpResponseMessage batch)> GetBoth(
        string stdPath, string stdCid, string batchPath, string batchCid)
    {
        var stdResp = await GetOn(StdUrl, stdPath, stdCid);
        var batchResp = await GetOn(BatchUrl, batchPath, batchCid);
        return (stdResp, batchResp);
    }

    private async Task<(HttpResponseMessage std, HttpResponseMessage batch)> DeleteBoth(
        string stdPath, string stdCid, string batchPath, string batchCid)
    {
        var stdResp = await DeleteOn(StdUrl, stdPath, stdCid);
        var batchResp = await DeleteOn(BatchUrl, batchPath, batchCid);
        return (stdResp, batchResp);
    }

    private static async Task<(string std, string batch)> ReadBoth(HttpResponseMessage std, HttpResponseMessage batch)
    {
        return (await std.Content.ReadAsStringAsync(), await batch.Content.ReadAsStringAsync());
    }

    // ── Raw HTTP helpers ──

    private static async Task<string> RegisterOn(string baseUrl, string name)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        var json = JsonSerializer.Serialize(new { client_name = name, version = "1.0.0-test" });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/eca/clients") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("client_id").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostOn(string baseUrl, string path, object body, string clientId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> GetOn(string baseUrl, string path, string clientId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> DeleteOn(string baseUrl, string path, string clientId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        using var req = new HttpRequestMessage(HttpMethod.Delete, path);
        req.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        return await client.SendAsync(req);
    }
}