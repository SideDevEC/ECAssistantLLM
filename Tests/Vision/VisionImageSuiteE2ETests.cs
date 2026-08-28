using System.Text;
using System.Text.Json;
using Xunit;

namespace ECAssistant.LLM.Tests.Vision;

/// <summary>
/// FULL-SYSTEM vision image suite (Category=E2E): runs against an EXTERNALLY
/// started ECAssistantLLM server (in-process model loading inside testhost
/// stalls — start the server binary instead and point the suite at it):
///   ECA_VISION_BASEURL=http://localhost:8423 dotnet test --filter VisionImageSuite
/// Registers a client, sends real images: single image, multiple images in one
/// request, and follow-up conversation that must remember image content.
/// All tests no-op (pass) when ECA_VISION_BASEURL is not set.
/// </summary>
[Trait("Category", "E2E")]
public class VisionImageSuiteE2ETests : IAsyncLifetime
{
    // Instance properties (not static) — LDC enforcement: no static mutable fields
    private string? BaseUrl => Environment.GetEnvironmentVariable("ECA_VISION_BASEURL");
    private string ModelId => Environment.GetEnvironmentVariable("ECA_VISION_MODEL_ID") ?? "main-vision";
    private HttpClient _http = null!;
    private string _clientId = "";

    // 320x200 PNG: top half green, bottom half yellow
    private const string GreenYellowImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAUAAAADICAIAAAAWZq/8AAABuklEQVR42u3TgQkAAAjDsJ3u53rHJJALCk0mQCsJwMCAgQEDg4EBAwMGBgwMBgYMDBgYDAwYGDAwYGAwMGBgwMCAgcHAgIEBA4OBAQMDBgYMDAYGDAwYGDAwGBgwMGBgMDBgYMDAgIHBwICBAQODgQEDAwYGDAwGBgwMGBgwMBgYMDBgYDAwYGDAwICBwcCAgQEDAwYGAwMGBgwMBgYMDBgYMDAYGDAwYGDAwGBgwMCAgcHAgIEBAwMGBgMDBgYMDAYGDAwYGDAwGBgwMGBgwMBgYMDAgIHBwICBAQMDBgYDAwYGDAwYGB7YBVpJAAYGDAwYGAwMGBgwMGBgMDBgYMDAYGDAwICBAQODgQEDAwYGDAwGBgwMGBgMDBgYMDBgYDAwYGDAwICBwcCAgQEDg4EBAwMGBgwMBgYMDBgYDKwCGBgwMGBgMDBgYMDAgIHBwICBAQODgQEDAwYGDAwGBgwMGBgwMBgYMDBgYDAwYGDAwICBwcCAgQEDAwYGAwMGBgwMBgYMDBgYMDAYGDAwYGAwMGBgwMCAgcHAgIEBAwMGBgMDBgYMDAYGDAwYGDAwGBgwMGBgwMDwwAGHoJg6Jam05QAAAABJRU5ErkJggg==";

    // 200x200 PNG: solid blue
    private const string BlueImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAMgAAADICAIAAAAiOjnJAAABcklEQVR42u3SQQkAAAjAwPUvrSkEHweXYKwaOCABxsJYGAuMhbEwFhgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAsjIWxwFgYC2OBsTAWxgJjYSyMBcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhgLY2EsMBbGwlggAcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAsjIWxwFgYC2OBsTAWxgJjYSyMBcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbEwlgQYC2NhLDAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhiLZxZ3zqzWpRO+pAAAAABJRU5ErkJggg==";

    // 200x200 PNG: solid red
    private const string RedImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAMgAAADICAIAAAAiOjnJAAABcklEQVR42u3SMQ0AAAjAsPk3DSY4OJpUwbKm4JwEGAtjYSwwFsbCWGAsjIWxwFgYC2OBsTAWxgJjYSyMBcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAsjIWxwFgYC2OBBBgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAsjIWxwFgYC2OBsTAWxgJjYSyMBcbCWBgLjIWxMBYYC2NhLDAWxsJYYCyMhbHAWBgLY4GxMBbGAmNhLIwFxsJYGAuMhbEwFhgLY2EsMBbGwlhgLIyFscBYGAtjgbEwFsbCWBJgLIyFscBYGAtjgbEwFsYCY2EsjAXGwlgYC4yFsTAWGAtjYSwwFsbCWGAs3lnRh6zWL0rapgAAAABJRU5ErkJggg==";

    public async Task InitializeAsync()
    {
        if (BaseUrl == null) return;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var body = JsonSerializer.Serialize(new { client_name = "vision-e2e-tests", version = "1.0" });
        var resp = await _http.PostAsync($"{BaseUrl}/eca/clients",
            new StringContent(body, Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        _clientId = doc.RootElement.GetProperty("client_id").GetString()!;
    }

    public Task DisposeAsync()
    {
        _http?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> CompleteAsync(object body, int maxTokens = 80)
    {
        if (BaseUrl == null) return "(skipped: ECA_VISION_BASEURL not set)";
        var json = JsonSerializer.Serialize(new
        {
            model = ModelId,
            messages = body,
            max_tokens = maxTokens,
            temperature = 0
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("X-Client-Id", _clientId);
        using var resp = await _http.SendAsync(msg);
        Assert.True(resp.IsSuccessStatusCode, $"vision completion failed: {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static object Text(string t) => new { type = "text", text = t };
    private static object Img(string b64) => new { type = "image_url", image_url = new { url = "data:image/png;base64," + b64 } };
    private static object Msg(params object[] content) => new { role = "user", content };

    [Fact]
    public async Task SingleImage_IdentifiesColors()
    {
        var answer = await CompleteAsync(new[]
        {
            Msg(Text("The image has two horizontal halves of different colors. Name both colors."), Img(GreenYellowImageBase64))
        });
        if (BaseUrl == null) return;

        Assert.Contains("green", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yellow", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoImages_InOneRequest_DescribedDistinctly()
    {
        // Regression: adjacent media markers desynced the MTMD tokenizer — the
        // converter now separates every marker with a newline.
        var answer = await CompleteAsync(new[]
        {
            Msg(Text("Two images. Two words: color of first image, then color of second image."), Img(BlueImageBase64), Img(RedImageBase64))
        });
        if (BaseUrl == null) return;

        Assert.Contains("blue", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("red", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FollowUpConversation_RemembersImageContent()
    {
        var answer1 = await CompleteAsync(new[]
        {
            Msg(Text("What solid color is this image? One word."), Img(BlueImageBase64))
        }, 30);
        if (BaseUrl == null) return;
        Assert.Contains("blue", answer1, StringComparison.OrdinalIgnoreCase);

        var answer2 = await CompleteAsync(new object[]
        {
            Msg(Text("What solid color is this image? One word."), Img(BlueImageBase64)),
            new { role = "assistant", content = answer1 },
            Msg(Text("Now answer only with the color name you just said. Nothing else."))
        }, 30);

        Assert.Contains("blue", answer2, StringComparison.OrdinalIgnoreCase);
    }
}
