using System.Net;
using System.Text;
using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Server;

/// <summary>
/// Writes SSE (Server-Sent Events) streaming responses for OpenAI-compatible endpoints.
/// </summary>
public static class SseStreamer
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null  // keep PascalCase in DTOs, we use JsonPropertyName
    };

    /// <summary>
    /// Upper bound for buffered request bodies (10 MB). Requests declaring or
    /// exceeding this size are rejected before buffering.
    /// </summary>
    public const long MaxRequestBodyBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Stream tokens as SSE chunks (chat format, object = chat.completion.chunk).
    /// </summary>
    public static Task StreamAsync(
        HttpListenerResponse response,
        IAsyncEnumerable<string> tokenStream,
        string model,
        CancellationToken ct)
        => StreamSseAsync(
            response, tokenStream, ct,
            makeDelta: (token, chunkId) =>
            {
                var chunk = ChatCompletionChunk.Delta(model, token);
                chunk.Id = chunkId;
                return chunk;
            },
            makeFinish: chunkId =>
            {
                var chunk = ChatCompletionChunk.Finish(model);
                chunk.Id = chunkId;
                return chunk;
            });

    /// <summary>
    /// Stream tokens as SSE chunks for text completions (object = text_completion).
    /// Uses CompletionChunk format with choices[].text instead of choices[].delta.content.
    /// </summary>
    public static Task StreamCompletionAsync(
        HttpListenerResponse response,
        IAsyncEnumerable<string> tokenStream,
        string model,
        CancellationToken ct)
        => StreamSseAsync(
            response, tokenStream, ct,
            makeDelta: (token, chunkId) =>
            {
                var chunk = CompletionChunk.Delta(model, token);
                chunk.Id = chunkId;
                return chunk;
            },
            makeFinish: chunkId =>
            {
                var chunk = CompletionChunk.Finish(model);
                chunk.Id = chunkId;
                return chunk;
            });

    /// <summary>
    /// Shared SSE framing core: per-token delta events, a final finish_reason event,
    /// then the [DONE] marker. Format-specific chunk construction is injected.
    /// </summary>
    private static async Task StreamSseAsync(
        HttpListenerResponse response,
        IAsyncEnumerable<string> tokenStream,
        CancellationToken ct,
        Func<string, string, object> makeDelta,
        Func<string, object> makeFinish)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";

        var stream = response.OutputStream;
        var chunkId = Guid.NewGuid().ToString("N");

        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

        try
        {
            await foreach (var token in tokenStream.WithCancellation(ct))
            {
                var json = JsonSerializer.Serialize(makeDelta(token, chunkId), JsonOptions);
                await writer.WriteAsync($"data: {json}\n\n"); // explicit \n\n — WriteLineAsync would emit \r\n on Windows
            }

            // Final chunk with finish_reason
            var finishJson = JsonSerializer.Serialize(makeFinish(chunkId), JsonOptions);
            await writer.WriteAsync($"data: {finishJson}\n\n");

            // End of stream marker
            await writer.WriteAsync("data: [DONE]\n\n");
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or cancelled — normal
        }
        catch (HttpListenerException)
        {
            // Client disconnected mid-stream — the stream is dead. Do NOT attempt to
            // write an error event to it (would just throw again); drop silently.
        }
        catch (Exception ex)
        {
            // Send error as SSE event — but the stream may have died mid-iteration,
            // so guard the write itself.
            try
            {
                var errorJson = JsonSerializer.Serialize(new { error = new { message = ex.Message, type = "stream_error" } });
                await writer.WriteAsync($"data: {errorJson}\n\n");
            }
            catch
            {
                // Stream already dead (client disconnect) — nothing to report to.
            }
        }
        finally
        {
            // v-fix: a bare `await using` disposed OUTSIDE the catch blocks — a flush
            // against a dead client stream threw HttpListenerException uncaught.
            try { await writer.DisposeAsync(); } catch { /* client gone */ }
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
    /// Read JSON body from request, enforcing MaxRequestBodyBytes. Oversized or
    /// undersized-declared bodies return default (callers respond 400).
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken ct = default)
    {
        // Stream-read the body: buffer at most MaxRequestBodyBytes, discard anything
        // beyond that (up to a hard ceiling) so the client is not left blocked writing.
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        const long MaxDiscardBytes = 256 * 1024 * 1024;
        long total = 0;
        bool tooLarge = false;
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxRequestBodyBytes)
            {
                tooLarge = true;
                if (total > MaxDiscardBytes)
                    return default; // pathological body — give up draining
                continue; // drain-and-discard: never buffered
            }
            ms.Write(buffer, 0, read);
        }

        if (tooLarge)
            return default;

        var body = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(body))
            return default;
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
