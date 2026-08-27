using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Embeddings;

/// <summary>
/// Tests for the OpenAI-compatible embeddings endpoint.
/// </summary>
[Collection("Server")]
public class EmbeddingsTests
{
    private readonly TestServerFixture _fixture;

    public EmbeddingsTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Embeddings_With_Valid_Input_Returns_200()
          {
        var resp = await _fixture.PostJsonAsync("/v1/embeddings",
             new { model = "embeddings", input = "The capital of France is Paris." });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("data").EnumerateArray());
          }

    [Fact]
    public async Task Embeddings_Response_Has_Embedding_Vector()
          {
        var resp = await _fixture.PostJsonAsync("/v1/embeddings",
             new { model = "embeddings", input = "Hello world." });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("list", root.GetProperty("object").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("model").GetString()));

        var first = root.GetProperty("data")[0];
        Assert.Equal("embedding", first.GetProperty("object").GetString());
        var embedding = first.GetProperty("embedding");
        Assert.Equal(JsonValueKind.Array, embedding.ValueKind);
        Assert.NotEmpty(embedding.EnumerateArray().ToArray());
          }

    [Fact]
    public async Task Embeddings_With_Empty_Input_Returns_400()
          {
        var resp = await _fixture.PostJsonAsync("/v1/embeddings",
             new { model = "embeddings", input = "" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
          }

    [Fact]
    public async Task Embeddings_With_Specific_Model_Returns_200()
          {
        var resp = await _fixture.PostJsonAsync("/v1/embeddings",
             new { model = "embeddings", input = "Specific model request." });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("embeddings", doc.RootElement.GetProperty("model").GetString());
          }
}
