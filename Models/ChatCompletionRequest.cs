using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECAssistant.LLM.Models;

/// <summary>
/// OpenAI-compatible chat completion request.
/// </summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "main";

    [JsonPropertyName("messages")]
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("repeat_penalty")]
    public float? RepeatPenalty { get; set; }

    /// <summary>ECAssistant extension: routes to specific KV cache session.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }



    /// <summary>v13: grammar-constrained decision decoding — response is a structured
    /// decision envelope instead of free text. Local models only (server enforces via GBNF).</summary>
    [JsonPropertyName("structured")]
    public bool Structured { get; set; } = false;

    /// <summary>v14.10: caller-supplied GBNF grammar — constrains output to the given
    /// shape (e.g. VisionStructureResult from ECAssistantCore). Local models only;
    /// rejected with stream=true. Response comes back as normal message content.</summary>
    [JsonPropertyName("grammar")]
    public string? Grammar { get; set; }

    /// <summary>OpenAI-native function calling: server grammar-constrains generation against
    /// these schemas and returns message.tool_calls. Ignored in stream mode (400).</summary>
    [JsonPropertyName("tools")]
    public List<OpenAiToolSpec>? Tools { get; set; }

    /// <summary>v14.12.1: registered tool names. When set with structured=true, the
    /// decision grammar's toolcall.name rule is constrained to this union — the model
    /// cannot emit a tool name that is not registered. Null/empty → permissive grammar
    /// (back-compat). Local models only (structured path).</summary>
    [JsonPropertyName("tool_names")]
    public List<string>? ToolNames { get; set; }

    /// <summary>OpenAI tool_choice: "auto" | "none" | {type:"function",function:{name}}.
    /// "none" = tools declared but disabled. Other values default to auto.</summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonIgnore]
    public bool ToolsActive => Tools is { Count: > 0 } && !IsToolChoiceNone();

    public bool IsToolChoiceNone()
    {
        if (ToolChoice is not { } c) return false;
        if (c.ValueKind == JsonValueKind.String) return c.GetString() == "none";
        return false;
    }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>
    /// Plain text content. String form is stored verbatim; array form joins text parts
    /// and inserts an image marker per image part (see ChatMessageContentConverter).
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>Decoded image payloads from content parts (in order of appearance). Empty when none.</summary>
    public IReadOnlyList<VisionImage> Images { get; set; } = Array.Empty<VisionImage>();

    [JsonIgnore]
    public bool HasImages => Images.Count > 0;
}

/// <summary>One image from an image_url content part (base64 data URI decoded to raw bytes).</summary>
public sealed record VisionImage(string MimeType, byte[] Data);

public sealed class ChatMessageContentConverter : JsonConverter<List<ChatMessage>>
{
    /// <summary>llama.cpp MTMD default media marker — used as placeholder for each image part.</summary>
    public const string DefaultImageMarker = "<__image__>";

    public override List<ChatMessage> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Unexpected chat messages token type: {reader.TokenType}");

        var messages = new List<ChatMessage>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject) continue;

            var msg = new ChatMessage();
            var text = new StringBuilder();
            var images = new List<VisionImage>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var prop = reader.GetString();
                if (!reader.Read()) break;
                if (reader.TokenType is JsonTokenType.Comment)
                { reader.Skip(); continue; }

                switch (prop)
                {
                    case "role":
                        // v-fix: a non-string role (number/bool) made Utf8JsonReader.GetString()
                        // throw InvalidOperationException → 500 instead of a 400-style rejection.
                        if (reader.TokenType == JsonTokenType.String)
                            msg.Role = reader.GetString() ?? "user";
                        else
                            reader.Skip();
                        break;
                    case "content":
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            text.Append(reader.GetString());
                        }
                        else if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            ParseContentParts(ref reader, text, images);
                        }
                        else if (reader.TokenType is JsonTokenType.Null or JsonTokenType.Number
                                 or JsonTokenType.True or JsonTokenType.False)
                        {
                            // Tolerate non-string content (null / numbers) — treated as empty.
                        }
                        else throw new JsonException($"Unexpected message content token type: {reader.TokenType}");
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            msg.Content = text.ToString();
            msg.Images = images;
            messages.Add(msg);
        }

        return messages;
    }

    private static void ParseContentParts(ref Utf8JsonReader reader, StringBuilder text, List<VisionImage> images)
    {
        var depth = 0;
        // Iterate part objects in the array
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray && depth == 0) return;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var part = JsonDocument.ParseValue(ref reader);
                var root = part.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "text" && root.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    text.Append(txt.GetString());
                else if (type == "image_url")
                {
                    var url = root.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object && iu.TryGetProperty("url", out var u)
                        ? u.GetString()
                        : null;
                    var img = ParseDataUri(url);
                    if (img == null)
                        throw new JsonException("Only base64 data-URI image_url parts are supported by this server.");
                    // Adjacent media markers confuse the MTMD tokenizer (multi-image desync) —
                    // always frame each marker with newline text so chunks stay separated.
                    if (text.Length > 0 && !text.ToString().EndsWith('\n'))
                        text.Append('\n');
                    text.Append(DefaultImageMarker);
                    text.Append('\n');
                    images.Add(img);
                }
                // unknown part types ignored for forward compatibility
                continue;
            }

            // Track nesting for primitive/other tokens until EndArray at depth 0
            if (reader.TokenType == JsonTokenType.StartArray || reader.TokenType == JsonTokenType.StartObject)
                depth++;
            else if (reader.TokenType == JsonTokenType.EndArray || reader.TokenType == JsonTokenType.EndObject)
                depth--;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<ChatMessage> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var m in value)
        {
            writer.WriteStartObject();
            writer.WriteString("role", m.Role);
            writer.WriteString("content", m.Content);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static VisionImage? ParseDataUri(string? url)
    {
        const string prefix = "data:";
        if (string.IsNullOrEmpty(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var semi = url.IndexOf(';');
        var comma = url.IndexOf(',');
        if (semi < 0 || comma < semi || !url.Substring(semi + 1, comma - semi - 1).Equals("base64", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var data = Convert.FromBase64String(url[(comma + 1)..]);
            return new VisionImage(url[prefix.Length..semi], data);
        }
        catch (FormatException) { return null; }
    }
}