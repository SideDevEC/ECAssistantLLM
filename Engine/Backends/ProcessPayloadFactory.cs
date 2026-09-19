using System.Text.Json.Nodes;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine.Backends;

/// <summary>
/// Single source of truth for outgoing child llama-server chat payloads (process backend).
/// Both transcript-backed sessions (ProcessSession) and stateless requests
/// (ProcessStatelessClient) build their payloads here so that client-facing sampling
/// semantics are identical: the same fields the in-process path honors
/// (RequestRouter.CreateInferenceParams) are mapped onto the child's OpenAI endpoint —
/// including the same defaults (temperature 0.3, top_p 0.95, top_k 40, repeat_penalty 1.1).
/// </summary>
internal static class ProcessPayloadFactory
{
    // Stateless utility — no mutable state

    /// <summary>
    /// Builds a child /v1/chat/completions payload. <paramref name="grammar"/> switches on
    /// structured decoding (GBNF + thinking disabled so the envelope stays in `content`).
    /// </summary>
    public static JsonObject Build(
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        int maxTokens,
        ChatCompletionRequest? sampling,
        string? grammar,
        bool stream)
    {
        var messagesArray = new JsonArray();
        foreach (var msg in messages)
            messagesArray.Add(BuildOutboundMessage(msg));

        var payload = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = messagesArray,
            ["stream"] = stream, // sessions always stream internally — uniform parsing path
            ["max_tokens"] = maxTokens,

            // Sampling parity with the in-process path (CreateInferenceParams): explicit
            // defaults, never rely on child-side defaults (llama-server defaults differ,
            // e.g. temperature 1.0 — which lets structured output wander in the [^"\]
            // string rule and pad to max_tokens).
            ["temperature"] = sampling?.Temperature ?? 0.3f,
            ["top_p"] = sampling?.TopP ?? 0.95f,
            ["top_k"] = sampling?.TopK ?? 40,
            ["repeat_penalty"] = sampling?.RepeatPenalty ?? 1.1f,
        };

        var stop = sampling?.Stop;
        if (stop is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var s in stop) arr.Add(s);
            payload["stop"] = arr;
        }

        if (grammar != null)
        {
            // Grammar-constrained decoding on the child. enable_thinking=false keeps the
            // constrained output in `content` — with thinking enabled the chat template
            // routes the envelope into reasoning_content and pads content with whitespace
            // (verified empirically on the Prism runtime, 2026-09-19).
            payload["grammar"] = grammar;
            payload["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }

        return payload;
    }

    /// <summary>Serializes one message; image parts become base64 data-URI image_url entries.</summary>
    public static JsonObject BuildOutboundMessage(ChatMessage msg)
    {
        if (!msg.HasImages)
            return new JsonObject { ["role"] = msg.Role, ["content"] = msg.Content };

        var parts = new JsonArray();
        if (msg.Content.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = msg.Content });
        foreach (var img in msg.Images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{img.MimeType};base64,{Convert.ToBase64String(img.Data)}"
                }
            });
        }
        return new JsonObject { ["role"] = msg.Role, ["content"] = parts };
    }
}
