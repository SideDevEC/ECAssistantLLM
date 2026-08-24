using System.Net;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Writes SSE (Server-Sent Events) streaming responses for OpenAI-compatible chat completions.
/// </summary>
public static class SseStreamer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null  // keep PascalCase in DTOs, we use JsonPropertyName
    };

    /// <summary>
    /// Stream tokens as SSE chunks. Writes directly to the HttpListenerResponse output stream.
    /// </summary>
    public static async Task StreamAsync(
        HttpListenerResponse response,
        IAsyncEnumerable<string> tokenStream,
        string model,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";

        var stream = response.OutputStream;
        var chunkId = Guid.NewGuid().ToString("N");

        await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        try
        {
            await foreach (var token in tokenStream.WithCancellation(ct))
            {
                var chunk = ChatCompletionChunk.Delta(model, token);
                chunk.Id = chunkId;
                var json = JsonSerializer.Serialize(chunk, JsonOptions);
                await writer.WriteLineAsync($"data: {json}");
                await writer.WriteLineAsync(); // empty line = event boundary
            }

            // Final chunk with finish_reason
            var finishChunk = ChatCompletionChunk.Finish(model);
            finishChunk.Id = chunkId;
            var finishJson = JsonSerializer.Serialize(finishChunk, JsonOptions);
            await writer.WriteLineAsync($"data: {finishJson}");
            await writer.WriteLineAsync();

            // End of stream marker
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or cancelled — normal
        }
        catch (Exception ex)
        {
            // Send error as SSE event
            var errorJson = JsonSerializer.Serialize(new { error = new { message = ex.Message, type = "stream_error" } });
            await writer.WriteLineAsync($"data: {errorJson}");
            await writer.WriteLineAsync();
        }
    }

    /// <summary>
    /// Write a JSON response with status code.
    /// </summary>
    public static async Task WriteJsonAsync(HttpListenerResponse response, object data, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>
    /// Write a plain text response.
    /// </summary>
    public static async Task WriteTextAsync(HttpListenerResponse response, string text, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>
    /// Read JSON body from request.
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken ct = default)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return default;
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}