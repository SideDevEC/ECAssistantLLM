using System.Text.Json;
using ECAssistant.LLM.Models;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// v13 Parses the grammar-forced decision envelope into a typed DTO.
/// Grammar guarantees valid JSON; this adds shape validation and lenient
/// error reporting for defense in depth.
/// Stateless utility — no mutable state.
/// </summary>
public static class StructuredDecoder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Decode raw model output into a DecisionEnvelope. Throws on invalid shape.</summary>
    public static DecisionEnvelope Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDecisionException("Empty model output");

        DecisionEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DecisionEnvelope>(raw, Options)
                       ?? throw new InvalidDecisionException("Null envelope");
        }
        catch (JsonException ex)
        {
            throw new InvalidDecisionException($"Invalid JSON: {ex.Message}");
        }

        if (!envelope.HasAnswer && !envelope.HasToolCalls)
            throw new InvalidDecisionException("Envelope has neither answer nor toolcalls");

        if (envelope.HasToolCalls)
        {
            foreach (var call in envelope.ToolCalls!)
            {
                if (string.IsNullOrWhiteSpace(call.Name))
                    throw new InvalidDecisionException("Tool call with empty name");
            }
        }

        return envelope;
    }
}

/// <summary>Envelope shape violation (should be impossible under the grammar — defense in depth).</summary>
public sealed class InvalidDecisionException : Exception
{
    public InvalidDecisionException(string message) : base(message) { }
}
