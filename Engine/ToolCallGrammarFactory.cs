using System.Text;
using System.Text.Json;

namespace ECAssistant.LLM.Engine;

/// <summary>
/// Builds a GBNF grammar that forces the model's output into the OpenAI
/// tool_calls wire shape: a JSON array of {"type":"function","function":{"name":...,
/// "arguments":{...schema-constrained...}}} objects. Per-tool name/args rules are
/// alternated so the grammar enforces each tool's own parameter schema.
/// The arguments member is generated as a JSON object here (easier to constrain);
/// ToolCallDecoder + the router convert it to the OpenAI string form.
/// Stateless utility — no mutable state.
/// </summary>
public static class ToolCallGrammarFactory
{
    /// <summary>Root rule name of the generated grammar.</summary>
    public const string Root = "root";

    public static string Build(IReadOnlyList<Models.OpenAiToolSpec> tools, int maxParallelToolCalls = 3)
    {
        if (maxParallelToolCalls < 1) maxParallelToolCalls = 1;
        var valid = tools.Where(t => t.IsValid).ToList();
        if (valid.Count == 0)
            throw new ArgumentException("At least one valid function tool is required", nameof(tools));

        var sb = new StringBuilder();
        // Bounded repetition: unbounded arrays let the model loop objects forever at
        // low temp, truncating at max_tokens mid-JSON (flaky 422s on both paths).
        // The bound (inference.max_parallel_tool_calls, default 3) forces "]" after
        // maxParallelToolCalls+1 calls, guaranteeing parseable output within budget.
        sb.AppendLine($"root ::= \"[\" ws toolcall (\",\" ws toolcall){{0,{maxParallelToolCalls - 1}}} ws \"]\"");
        sb.AppendLine(JsonSchemaGrammarConverter.StringRule);
        sb.AppendLine("integer ::= \"-\"? [0-9]+");
        sb.AppendLine("number ::= integer (\".\" [0-9]+)?");
        sb.AppendLine("boolean ::= \"true\" | \"false\"");
        sb.AppendLine("json-value ::= string | integer | number | boolean | \"null\" | \"{\" ws (string \":\" ws json-value (\",\" ws string \":\" ws json-value)*)? ws \"}\" | \"[\" ws (json-value (\",\" ws json-value)*)? ws \"]\"");
        sb.AppendLine("ws ::= [ \\t\\n]*");

        var names = new JsonSchemaGrammarConverter.UniqueRuleNames();
        var callAlts = new List<string>();

        foreach (var tool in valid)
        {
            var fn = tool.Function!;
            var callRule = names.Next("call-" + Safe(fn.Name));
            var nameAlt = JsonSchemaGrammarConverter.JsonEscapeAlternation(fn.Name);
            var argsRule = names.Next("args-" + Safe(fn.Name));

            var schema = fn.Parameters is { } p && p.ValueKind == JsonValueKind.Object
                ? p
                : default(JsonElement?);
            if (schema is { } s)
            {
                JsonSchemaGrammarConverter.Convert(s, argsRule, sb, names);
            }
            else
            {
                // Permissive fallback: any string-keyed object with JSON values.
                sb.AppendLine($"{argsRule} ::= \"{{\" ws (string \":\" ws json-value (\",\" ws string \":\" ws json-value)*)? ws \"}}\"");
            }

            sb.AppendLine(
                $"{callRule} ::= \"{{\" ws \"\\\"type\\\"\" ws \":\" ws \"\\\"function\\\"\" ws \",\" ws " +
                $"\"\\\"function\\\"\" ws \":\" ws \"{{\" ws \"\\\"name\\\"\" ws \":\" ws {nameAlt} ws \",\" ws " +
                $"\"\\\"arguments\\\"\" ws \":\" ws {argsRule} ws \"}}\" ws \"}}\"");
            callAlts.Add(callRule);
        }

        sb.AppendLine($"toolcall ::= {string.Join(" | ", callAlts)}");
        return sb.ToString();
    }

    private static string Safe(string name)
        => new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
