using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Models;

/// <summary>
/// Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class ModelsTests
{
    private readonly TestServerFixture _fixture;

    public ModelsTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetV1Models_Returns_List_With_Model_Data()
    {
        using var resp = await _fixture.GetAsClientAsync("/v1/models");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("list", root.GetProperty("object").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.NotEmpty(data.EnumerateArray().ToArray());
    }

    [Fact]
    public async Task GetV1Models_Each_Has_Required_Fields()
    {
        using var resp = await _fixture.GetAsClientAsync("/v1/models");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(m.GetProperty("id").GetString()));
            Assert.Equal("model", m.GetProperty("object").GetString());
            Assert.Equal(JsonValueKind.Number, m.GetProperty("created").ValueKind);
            Assert.Equal("ecassistant", m.GetProperty("owned_by").GetString());
            Assert.Equal(JsonValueKind.True, m.GetProperty("loaded").ValueKind);
            Assert.True(m.TryGetProperty("is_embedding", out var isEmbed) &&
                 (isEmbed.ValueKind == JsonValueKind.True || isEmbed.ValueKind == JsonValueKind.False));
        }
    }

    [Fact]
    public async Task GetEcaModels_Returns_Models_Array()
    {
        using var resp = await _fixture.GetAsClientAsync("/eca/models");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // /eca/models returns { models: [...] } with PascalCase model info
        var models = root.GetProperty("models");
        Assert.Equal(JsonValueKind.Array, models.ValueKind);
        Assert.NotEmpty(models.EnumerateArray().ToArray());

        foreach (var m in models.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(m.GetProperty("Id").GetString()));
            Assert.True(m.GetProperty("IsLoaded").GetBoolean());
            Assert.True(m.TryGetProperty("IsEmbedding", out _));
        }
    }

    [Fact]
    public async Task Both_Model_Endpoints_Return_Same_Count()
    {
        using var v1 = await _fixture.GetAsClientAsync("/v1/models");
        var v1Body = await v1.Content.ReadAsStringAsync();
        var v1Count = JsonDocument.Parse(v1Body).RootElement
             .GetProperty("data").GetArrayLength();

        using var eca = await _fixture.GetAsClientAsync("/eca/models");
        var ecaBody = await eca.Content.ReadAsStringAsync();
        var ecaCount = JsonDocument.Parse(ecaBody).RootElement
             .GetProperty("models").GetArrayLength();

        Assert.Equal(v1Count, ecaCount);
        Assert.True(v1Count >= 2, "Expected at least 2 models (main + embeddings)");
    }
}