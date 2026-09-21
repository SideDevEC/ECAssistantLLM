using System.Text;
using System.Text.Json;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Converts a (subset of) JSON Schema into GBNF grammar fragments the llama.cpp
/// sampler can enforce. Supported: string (with enum), integer, number, boolean,
/// null, array, object with properties/required. Anything else degrades to a
/// generic JSON value rule — required-ness for degraded properties is enforced
/// downstream by ToolCallDecoder instead of the grammar.
///
/// Emission is strictly two-phase: the schema tree is walked first, collecting
/// named rule definitions into a list; only afterwards are they written to the
/// StringBuilder. No recursive side effects can interleave with in-flight
/// emissions (this ordering guarantee is deliberate — see 2026-09-21 fix).
/// Stateless utility — no mutable state.
/// </summary>
public static class JsonSchemaGrammarConverter
{
    /// <summary>GBNF string rule — mirrors DecisionGrammar: simple negated class, no
    /// hex-range character classes (unreliable in the llama.cpp GBNF compiler).</summary>
    public const string StringRule = """
string ::= "\"" ( [^"\\] | "\\" ( ["\\bfnrt] | "u" [0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F] ) )* "\""
""";

    /// <summary>
    /// Emits GBNF rules for a JSON-schema value. The top rule named
    /// <paramref name="ruleName"/> matches one value of the schema; nested rules
    /// are appended to <paramref name="sb"/> after the full tree is walked.
    /// </summary>
    public static void Convert(JsonElement schema, string ruleName, StringBuilder sb, UniqueRuleNames names)
    {
        var defs = new List<KeyValuePair<string, string>>();
        var expr = EmitValue(schema, ruleName, defs, names);
        foreach (var def in defs)
            sb.AppendLine($"{def.Key} ::= {def.Value}");
        sb.AppendLine($"{ruleName} ::= {expr}");
    }

    /// <summary>
    /// Returns the GBNF expression for one schema value, appending any nested
    /// rule definitions (in post-order) to <paramref name="defs"/>. Never writes
    /// to the StringBuilder directly.
    /// </summary>
    private static string EmitValue(JsonElement schema, string ruleName, List<KeyValuePair<string, string>> defs, UniqueRuleNames names)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.Undefined)
            return "json-value"; // permissive any-JSON
        if (schema.ValueKind != JsonValueKind.Object)
            return "json-value";

        var type = ReadType(schema);

        // enum → alternation of quoted literals (works for string enums)
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array && enumEl.GetArrayLength() > 0)
        {
            var alts = enumEl.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? ""
                    : e.GetRawText())
                .Select(JsonEscapeAlternation)
                .ToList();
            return string.Join(" | ", alts);
        }

        switch (type)
        {
            case "string": return "string";
            case "integer": return "integer";
            case "number": return "number";
            case "boolean": return "boolean";
            case "null": return "\"null\"";
        }

        if (type == "array")
        {
            var itemRule = names.Next(ruleName + "-item");
            var items = schema.TryGetProperty("items", out var itemsEl) ? itemsEl : default;
            var itemExpr = EmitValue(items, itemRule, defs, names);
            defs.Add(new(itemRule, itemExpr));
            return $"\"[\" ws ({itemRule} (\",\" ws {itemRule})*)? ws \"]\"";
        }

        if (type == "object")
            return EmitObject(schema, ruleName, defs, names);

        return "json-value";
    }

    private static string EmitObject(JsonElement schema, string ruleName, List<KeyValuePair<string, string>> defs, UniqueRuleNames names)
    {
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object ||
            !props.EnumerateObject().Any())
        {
            // Schema-less object: any string-keyed object with JSON values.
            return "\"{\" ws (string \":\" ws json-value (\",\" ws string \":\" ws json-value)*)? ws \"}\"";
        }

        var propsList = props.EnumerateObject().ToList();
        var required = new List<string>();
        if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
            required = reqEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        // Pair rules: one per property, value rule per property schema (post-order).
        var pairRefs = new List<string>();
        foreach (var p in propsList)
        {
            var pairRule = names.Next(ruleName + "-" + Safe(p.Name) + "-pair");
            var valueRule = names.Next(ruleName + "-" + Safe(p.Name) + "-value");
            var valueExpr = EmitValue(p.Value, valueRule, defs, names);
            defs.Add(new(valueRule, valueExpr));
            defs.Add(new(pairRule, "\"\\\"" + GbnfEscape(p.Name) + "\\\"\" ws \":\" ws " + valueRule));
            pairRefs.Add(pairRule);
        }

        var objRule = names.Next(ruleName + "-obj");
        var requiredPairs = propsList
            .Where(p => required.Contains(p.Name))
            .Select(p => pairRefs[propsList.IndexOf(p)])
            .ToList();
        var optionalPairs = propsList
            .Where(p => !required.Contains(p.Name))
            .Select(p => pairRefs[propsList.IndexOf(p)])
            .ToList();

        // Required properties are MANDATORY in the grammar (fixed declared order);
        // optional properties may follow as a comma-separated tail.
        string objExpr;
        if (optionalPairs.Count == 0)
        {
            objExpr = "\"{\" ws " + string.Join(" \",\" ws ", requiredPairs) + " ws \"}\"";
        }
        else
        {
            var optRule = names.Next(ruleName + "-opt");
            defs.Add(new(optRule, string.Join(" | ", optionalPairs)));
            objExpr = "\"{\" ws " + string.Join(" \",\" ws ", requiredPairs) +
                      " (\",\" ws " + optRule + ")* ws \"}\"";
        }
        defs.Add(new(objRule, objExpr));
        return objRule;
    }

    private static string ReadType(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
            if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0)
            {
                // Prefer the non-null member for grammar purposes
                foreach (var x in t.EnumerateArray())
                {
                    var s = x.GetString();
                    if (s is not null && s != "null") return s;
                }
                return t[0].GetString() ?? "";
            }
        }
        return "";
    }

    private static string Safe(string name)
        => new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>Escape a JSON string literal for a GBNF quoted-alternation. GBNF uses
    /// backslash escapes compatible with JSON for \".</summary>
    /// <summary>Renders a JSON string value as a GBNF terminal including the JSON
    /// quotes themselves: "create" → "\"create\"" (outer GBNF quotes, inner escaped
    /// JSON quotes — matches llama.cpp json-schema-to-grammar output).</summary>
    public static string JsonEscapeAlternation(string literal)
    {
        var sb = new StringBuilder("\"");
        sb.Append("\\\"");
        foreach (var ch in literal)
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch == '\n') sb.Append("\\n");
            else if (ch == '\t') sb.Append("\\t");
            else sb.Append(ch);
        }
        sb.Append("\\\"");
        sb.Append('"');
        return sb.ToString();
    }

    private static string GbnfEscape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");

    /// <summary>Allocates unique rule names across a whole grammar build.
    /// Stateless helper type — no shared state.</summary>
    public sealed class UniqueRuleNames
    {
        private readonly HashSet<string> _used = new();
        public string Next(string seed)
        {
            var name = seed;
            var i = 2;
            while (!_used.Add(name))
                name = $"{seed}-{i++}";
            return name;
        }
    }
}
