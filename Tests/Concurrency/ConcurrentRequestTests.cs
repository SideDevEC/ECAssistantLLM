using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Concurrency;

/// <summary>
/// Tests for concurrent access to the LLM server.
/// Multiple clients hitting the server simultaneously should all succeed.
/// </summary>
[Collection("Server")]
public class ConcurrentRequestTests
{
    private readonly TestServerFixture _fixture;

    public ConcurrentRequestTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Parallel_Health_Checks_All_Return_200()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _fixture.GetAsClientAsync("/eca/health"));

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task Two_Clients_Create_Sessions_Simultaneously()
    {
        var t1 = _fixture.CreateSessionAsync(sessionId: "conc-1-" + Guid.NewGuid().ToString("N")[..8], clientName: "client-a");
        var t2 = _fixture.CreateSessionAsync(sessionId: "conc-2-" + Guid.NewGuid().ToString("N")[..8], clientName: "client-b");

        var r1 = await t1; var r2 = await t2;

        Assert.NotEqual(r1.clientId, r2.clientId);
        Assert.NotEqual(r1.sessionId, r2.sessionId);

        // Cleanup
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{r1.sessionId}", r1.clientId);
        await _fixture.DeleteAsClientAsync($"/eca/sessions/{r2.sessionId}", r2.clientId);
    }

    [Fact]
    public async Task Five_Parallel_Session_Creations_All_Succeed()
    {
        var tasks = Enumerable.Range(0, 5)
            .Select(i => _fixture.CreateSessionAsync(
                sessionId: $"par-{i}-" + Guid.NewGuid().ToString("N")[..8],
                clientName: $"parallel-{i}"));

        var results = await Task.WhenAll(tasks);
        Assert.Equal(5, results.Length);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.clientId)));
        Assert.Equal(5, results.Select(r => r.sessionId).Distinct().Count());

        // Cleanup
        foreach (var (cid, sid) in results)
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sid}", cid);
    }

    [Fact]
    public async Task Concurrent_Chat_Completions_Both_Complete()
    {
        var (cid1, sid1) = await _fixture.CreateSessionAsync(sessionId: "chat-conc-1", clientName: "chat-a");
        var (cid2, sid2) = await _fixture.CreateSessionAsync(sessionId: "chat-conc-2", clientName: "chat-b");

        try
        {
            // Prefill both
            await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sid1}/prefill", new { text = "You are a helpful assistant." }, cid1);
            await _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sid2}/prefill", new { text = "You are a helpful assistant." }, cid2);

            // Both send chat completions simultaneously
            var t1 = _fixture.PostJsonAsClientAsync("/v1/chat/completions",
                new
                {
                    model = "main",
                    session_id = sid1,
                    stream = false,
                    messages = new[] { new { role = "user", content = "Say hi" } },
                    max_tokens = 16
                }, cid1);

            var t2 = _fixture.PostJsonAsClientAsync("/v1/chat/completions",
                new
                {
                    model = "main",
                    session_id = sid2,
                    stream = false,
                    messages = new[] { new { role = "user", content = "Say hello" } },
                    max_tokens = 16
                }, cid2);

            var r1 = await t1; var r2 = await t2;

            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

            var body1 = await r1.Content.ReadAsStringAsync();
            var body2 = await r2.Content.ReadAsStringAsync();
            Assert.Contains("chat.completion", body1);
            Assert.Contains("chat.completion", body2);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sid1}", cid1);
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sid2}", cid2);
        }
    }

    [Fact]
    public async Task Concurrent_Prefill_On_Different_Sessions_Both_Complete()
    {
        var (cid1, sid1) = await _fixture.CreateSessionAsync(sessionId: "prefill-conc-1", clientName: "pf-a");
        var (cid2, sid2) = await _fixture.CreateSessionAsync(sessionId: "prefill-conc-2", clientName: "pf-b");

        try
        {
            var t1 = _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sid1}/prefill", new { text = "Context block A." }, cid1);
            var t2 = _fixture.PostJsonAsClientAsync(
                $"/eca/sessions/{sid2}/prefill", new { text = "Context block B." }, cid2);

            var r1 = await t1; var r2 = await t2;

            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        }
        finally
        {
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sid1}", cid1);
            await _fixture.DeleteAsClientAsync($"/eca/sessions/{sid2}", cid2);
        }
    }
}