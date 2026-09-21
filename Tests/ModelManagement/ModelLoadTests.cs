using System.Net;
using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.ModelManagement;

/// <summary>
/// Tests for runtime model management: /eca/models/load, /eca/models/unload.
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class ModelLoadTests
{
    private readonly TestServerFixture _fixture;

    public ModelLoadTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unload_Unknown_Model_Returns_404()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/models/unload",
            new { id = "nonexistent-model" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Unload_With_Empty_Body_Returns_400()
    {
        using var resp = await _fixture.PostRawAsync("/eca/models/unload", "");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Load_With_Missing_Id_Returns_400()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/models/load",
            new { path = "models/test.gguf" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }


    [Fact]
    public async Task Load_With_NonExistent_Path_Returns_Error()
    {
        using var resp = await _fixture.PostJsonAsync("/eca/models/load",
            new { id = "test-fail", path = "models/nonexistent.gguf", gpu_layers = 0, context_size = 2048, threads = -1 });

        // Should be 400, 500, or a graceful error — not 200 (can't load non-existent model)
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetModels_After_Unload_Reflects_Removal()
    {
        // Get initial model count
        using var before = await _fixture.GetAsClientAsync("/v1/models");
        var beforeBody = await before.Content.ReadAsStringAsync();
        var beforeCount = JsonDocument.Parse(beforeBody).RootElement.GetProperty("data").GetArrayLength();

        // Unload the embeddings model (safe — no sessions depend on it)
        using var unloadResp = await _fixture.PostJsonAsync("/eca/models/unload",
            new { id = "embeddings" });

        if (unloadResp.StatusCode == HttpStatusCode.OK)
        {
            // Verify model count decreased
            using var after = await _fixture.GetAsClientAsync("/v1/models");
            var afterBody = await after.Content.ReadAsStringAsync();
            var afterCount = JsonDocument.Parse(afterBody).RootElement.GetProperty("data").GetArrayLength();
            Assert.Equal(beforeCount - 1, afterCount);

            // Reload it so other tests still work
            var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
            await _fixture.PostJsonAsync("/eca/models/load",
                new
                {
                    id = "embeddings",
                    path = Path.Combine(modelsDir, "all-MiniLM-L6-v2-q4_k_m.gguf"),
                    gpu_layers = 0,
                    context_size = 2048,
                    threads = -1,
                    is_embedding = true
                });
        }
    }
}