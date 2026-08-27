using System.Text.Json;
using ECAssistant.LLM.Models;
using Xunit;

namespace ECAssistant.LLM.Tests.ChatCompletions;

/// <summary>
/// Multimodal content parsing: plain strings, content-part arrays, data-URI images.
/// Pure JSON — no model loading required.
/// </summary>
public class ChatMessageContentConverterTests
{
    private static ChatCompletionRequest Deserialize(string json)
        => JsonSerializer.Deserialize<ChatCompletionRequest>(json)!;

    [Fact]
    public void PlainStringContent_Parses()
    {
        var req = Deserialize("""{"messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}""");
        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("sys", req.Messages[0].Content);
        Assert.Equal("hi", req.Messages[1].Content);
        Assert.False(req.Messages[1].HasImages);
    }

    [Fact]
    public void NullContent_ToleratedAsEmpty()
    {
        var req = Deserialize("""{"messages":[{"role":"assistant","content":null}]}""");
        Assert.Equal("", req.Messages[0].Content);
    }

    [Fact]
    public void ExtraMessageProperties_Ignored()
    {
        var req = Deserialize("""{"messages":[{"role":"user","content":"x","name":"n","custom":{"a":1}}]}""");
        Assert.Equal("x", req.Messages[0].Content);
    }

    [Fact]
    public void ContentParts_TextAndImage_Parsed()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var url = "data:image/png;base64," + b64;
        var json = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"what is this?\"}," +
            $"{{\"type\":\"image_url\",\"image_url\":{{\"url\":\"{url}\"}}}}" +
            "]}]}";
        var req = Deserialize(json);
        var msg = req.Messages[0];
        Assert.True(msg.HasImages);
        Assert.Single(msg.Images);
        Assert.Equal("image/png", msg.Images[0].MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, msg.Images[0].Data);
        // Text part + one marker per image, marker present in Content for MTMD prompt building
        Assert.StartsWith("what is this?", msg.Content);
        Assert.Contains(ChatMessageContentConverter.DefaultImageMarker, msg.Content);
    }

    [Fact]
    public void NonDataUriImage_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => Deserialize(
            """{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://example.com/a.png"}}]}]}"""));
    }

    [Fact]
    public void MultipleMessages_MultipleImages_OrderPreserved()
    {
        var b64 = "AAAA";
        var img = (string mime) => "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:" + mime + ";base64," + b64 + "\"}}";
        var json = "{\"messages\":[" +
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"}," + img("image/jpeg") + "]}," +
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"two\"}," + img("image/webp") + "]}" +
            "]}";
        var req = Deserialize(json);
        Assert.Single(req.Messages[0].Images);
        Assert.Single(req.Messages[1].Images);
        Assert.Equal("image/jpeg", req.Messages[0].Images[0].MimeType);
        Assert.Equal("image/webp", req.Messages[1].Images[0].MimeType);
    }
}
