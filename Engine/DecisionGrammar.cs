namespace ECAssistant.LLM.Engine;

/// <summary>
/// v13 GBNF grammar that forces the model's output into the decision envelope
/// JSON shape (see DecisionEnvelope). Injected at the sampler so ANY model
/// physically cannot emit invalid output — no prompt discipline required.
/// v14.12.1: BuildGbnf(toolNames) swaps the permissive name rule for a union of the
/// registered tool names — small models cannot hallucinate an unknown tool name.
/// Stateless utility — no mutable state.
/// </summary>
public static class DecisionGrammar
{
    /// <summary>
    /// GBNF source. Root rule: {"thinking": string, ("answer": string | "toolcalls": [call])}
    /// NOTE: the string rule intentionally uses the simple negated class [^"\\] — complex
    /// hex-escape character ranges (\x00-\x1F etc.) in the character class are not reliably
    /// parsed by the llama.cpp GBNF compiler and made the whole rule misbehave, letting raw
    /// newlines/tabs through. Unescaped control chars that still slip through are repaired
    /// by StructuredDecoder (defense in depth).
    /// The `name` rule is permissive here (any JSON string); BuildGbnf constrains it.
    /// </summary>
    public const string Gbnf = """
root ::= envelope
envelope ::= "{" ws "\"thinking\"" ws ":" ws string ws ("," ws "\"answer\"" ws ":" ws string ws | "," ws "\"toolcalls\"" ws ":" ws "[" ws (toolcall ("," ws toolcall)*)? ws "]" ws) "}"
toolcall ::= "{" ws "\"name\"" ws ":" ws name ws "," ws "\"args\"" ws ":" ws object ws "}"
name ::= string
string ::= "\"" ( [^"\\] | "\\" ( ["\\bfnrt] | "u" [0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F] ) )* "\""
object ::= "{" ws (string ":" ws string ("," ws string ":" ws string)*)? ws "}"
ws ::= [ \t\n]*
""";

    /// <summary>Grammar root rule name.</summary>
    public const string Root = "root";

    /// <summary>
    /// v14.12.1: Build the grammar with toolcall.name constrained to the given tool
    /// names (GBNF alternation of quoted literals). Null/empty/invalid input returns
    /// the permissive Gbnf unchanged — callers never get a broken grammar.
    /// Pure function — no mutable state.
    /// </summary>
    // Stateless utility — no mutable state
    public static string BuildGbnf(IReadOnlyCollection<string>? toolNames, int maxToolCalls = 3)
    {
        if (maxToolCalls < 1) maxToolCalls = 1;
        // Bounded toolcalls array — mirrors ToolCallGrammarFactory: an unbounded
        // array lets low-temp models loop toolcalls until max_tokens truncates
        // the JSON mid-string. {0,max-1} forces "]" after max calls.
        var bounded = Gbnf.Replace("(\",\" ws toolcall)*", $"(\",\" ws toolcall){{0,{Math.Max(0, maxToolCalls - 1)}}}");
        if (toolNames is not { Count: > 0 })
            return bounded;

        var names = toolNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return bounded;

        // The grammar must match the tool name AS JSON EMITS IT — quotes included
        // ("EShellAgent", not the bare word): the permissive `string` rule emits the
        // quotes itself, so the union literals must carry them. JSON-escape the name,
        // then GBNF-encode that text as a literal (backslash and quote escaped) — the
        // same escape pattern the envelope rule uses for "thinking".
        var union = string.Join(" | ", names.Select(n =>
            "\"" + ("\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                .Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

        // Swap the permissive `name ::= string` rule for the constrained union.
        // Applied to the BOUNDED grammar — bounding must survive name substitution.
        return bounded.Replace("name ::= string", "name ::= " + union);
    }
}
