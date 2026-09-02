namespace ECAssistant.LLM.Engine;

/// <summary>
/// v13 GBNF grammar that forces the model's output into the decision envelope
/// JSON shape (see DecisionEnvelope). Injected at the sampler so ANY model
/// physically cannot emit invalid output — no prompt discipline required.
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
    /// </summary>
    public const string Gbnf = """
root ::= envelope
envelope ::= "{" ws "\"thinking\"" ws ":" ws string ws ("," ws "\"answer\"" ws ":" ws string ws | "," ws "\"toolcalls\"" ws ":" ws "[" ws (toolcall ("," ws toolcall)*)? ws "]" ws) "}"
toolcall ::= "{" ws "\"name\"" ws ":" ws string ws "," ws "\"args\"" ws ":" ws object ws "}"
string ::= "\"" ( [^"\\] | "\\" ( ["\\bfnrt] | "u" [0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F] ) )* "\""
object ::= "{" ws (string ":" ws string ("," ws string ":" ws string)*)? ws "}"
ws ::= [ \t\n]*
""";

    /// <summary>Grammar root rule name.</summary>
    public const string Root = "root";
}
