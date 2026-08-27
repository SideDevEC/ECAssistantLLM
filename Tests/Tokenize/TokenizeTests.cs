using System.Text.Json;
using ECAssistant.LLM.Tests.Fixtures;

namespace ECAssistant.LLM.Tests.Tokenize;

/// <summary>
/// Tests for the ECA tokenize endpoint (POST /eca/tokenize).
/// </summary>
[Collection("Server")]
[Trait("Category","E2E")]
    public class TokenizeTests
{
    private readonly TestServerFixture _fixture;

    public TokenizeTests(TestServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tokenize_With_Text_Returns_Tokens_And_TokenIds()
           {
        var resp = await _fixture.PostJsonAsync("/eca/tokenize",
             new { model = "main", text = "The quick brown fox" });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("tokens").GetInt32() > 0);
        var tokenIds = root.GetProperty("token_ids");
        Assert.Equal(JsonValueKind.Array, tokenIds.ValueKind);
        Assert.Equal(root.GetProperty("tokens").GetInt32(),
             tokenIds.EnumerateArray().Count());
           }

    [Fact]
    public async Task Tokenize_With_Empty_Text_Returns_400()
           {
        var resp = await _fixture.PostJsonAsync("/eca/tokenize",
             new { model = "main", text = "" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("invalid_request",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
           }

    [Fact]
    public async Task Tokenize_With_Empty_Body_Returns_400()
           {
        var resp = await _fixture.PostRawAsync("/eca/tokenize", "");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
           }

    [Fact]
    public async Task Tokenize_With_Specific_Model_Returns_200()
           {
        var resp = await _fixture.PostJsonAsync("/eca/tokenize",
             new { model = "main", text = "Hello, world! This is a test of the tokenizer." });

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("tokens").GetInt32() > 0);
        Assert.NotEmpty(doc.RootElement.GetProperty("token_ids").EnumerateArray().ToArray());
           }
}
