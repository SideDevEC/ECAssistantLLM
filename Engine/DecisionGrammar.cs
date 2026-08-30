namespace ECAssistant.LLM.Engine;

/// <summary>
/// v13 GBNF grammar that forces the model's output into the decision envelope
/// JSON shape (see DecisionEnvelope). Injected at the sampler so ANY model
/// physically cannot emit invalid output — no prompt discipline required.
/// Stateless utility — no mutable state.
/// </summary>
public static class DecisionGrammar
{
    /// <summary>GBNF source. Root rule: {"thinking": string, ("answer": string | "toolcalls": [call])}</summary>
    public const string Gbnf = """
root ::= "{" ws "\"thinking\"" ws ":" ws string ws "," ws body ws "}"
body ::= "\"answer\"" ws ":" ws string | "\"toolcalls\"" ws ":" ws "[" ws [ call (ws "," ws call)* ] ws "]"
call  ::= "{" ws "\"name\"" ws ":" ws string ws "," ws "\"args\"" ws ":" ws obj ws "}"
obj   ::= "{" ws [ string ws ":" ws string (ws "," ws string ws ":" ws string)* ] ws "}"
string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" ( ["\\bfnrt] | "u" [0-9a-fA-F]{4} ) )* "\""
ws ::= [ \t\n\r]*
""";

    /// <summary>Grammar root rule name.</summary>
    public const string Root = "root";
}
