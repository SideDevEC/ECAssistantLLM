# Structured Decision Decoding — Native Tool-Calling Architecture (v14)

**Summary:** Grammar-constrained JSON decoding in ECAssistantLLM produces `DecisionEnvelope` JSON → `StructuredDecisionAdapter.ParseDecision()` → `LLMDecision` directly. No tags anywhere. Model-agnostic, like OpenClaw/Hermes.

## Why
- OpenClaw never parses tags: providers enforce structured tool calls via API.
- LLamaSharp GBNF grammar physically constrains output shape — stronger than prompt discipline.
- Works for any model: local (grammar) or remote (OpenAI native `tool_calls`).

## Decision Envelope (single JSON contract)
```json
{"thinking": "...", "answer": "user-facing reply"}
{"thinking": "...", "toolcalls": [{"name": "ToolName", "args": {"argname": "string value"}}]}
```
Exactly one of `answer` | `toolcalls` per response. All args are strings.

## GBNF Grammar (server-side, injected at sampling)
```
root ::= envelope
envelope ::= "{" ws "\"thinking\"" ws ":" ws string ws ("," ws "\"answer\"" ws ":" ws string ws | "," ws "\"toolcalls\"" ws ":" ws "[" ws (toolcall ("," ws toolcall)*)? ws "]" ws) "}"
toolcall ::= "{" ws "\"name\"" ws ":" ws string ws "," ws "\"args\"" ws ":" ws object ws "}"
string ::= "\"" ( [^"\\] | "\\" ( ["\\bfnrt] | "u" [0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F] ) )* "\""
object ::= "{" ws (string ":" ws string ("," ws string ":" ws string)*)? ws "}"
ws ::= [ \t\n]*
```
- `tool_names` (v14.12.1): structured requests may carry registered tool names; RequestRouter builds `DecisionGrammar.BuildGbnf(req.ToolNames)` — toolcall.name constrained to the union. Absent/null → permissive grammar (back-compat, old callers unchanged).

## Pipeline (v14.7 — early termination)
```
Local:  Server → GBNF grammar → tokens stream → TryParseCompleteEnvelope() breaks at brace balance → DecisionEnvelope → StructuredDecisionAdapter.ParseDecision() → LLMDecision → Orchestrator
Remote: Provider → OpenAI tool_calls → HttpStreamingEngine.GenerateNativeToolsDecisionAsync() → StructuredDecisionAdapter.ParseDecision() → LLMDecision → Orchestrator
```

No `<lm>`, `<thinking>`, `<toolcall>`, `<output>` tags anywhere. The orchestrator consumes `LLMDecision` objects directly.

**Early termination:** The streaming loop checks brace balance + JSON parse after each token. As soon as the envelope JSON is complete (valid `DecisionEnvelope` with `answer` or `toolcalls`), generation stops — no wasted tokens after the grammar root matches. Saves ~70% generation time.

## Defense in Depth
- **GBNF grammar** constrains output shape at the sampler level
- **`StructuredDecoder.EscapeUnescapedControlChars()`** pre-processes raw model output to escape any control chars before JSON parsing (llama.cpp GBNF char classes aren't fully reliable)
- **`StructuredDecisionAdapter.ParseDecision()`** uses `JsonDocument` with fallback (thinking → answer if neither answer nor toolcalls)

## Classes (one per file, constructor-injected)
- `ECAssistantLLM/Engine/DecisionGrammar.cs` — GBNF template (stateless)
- `ECAssistantLLM/Engine/StructuredDecoder.cs` — envelope JSON → typed decision DTO + control char escaping
- `ECAssistantLLM/Models/DecisionEnvelope.cs` — immutable DTO
- `ECAssistantCore/Engine/StructuredDecisionAdapter.cs` — envelope JSON → `LLMDecision` (stateless, `ParseDecision()`)
- `ECAssistantCore/Engine/LLMDecision.cs` — decision DTO with `FromEnvelope()` factory + `Reasoning` property
- `ECAssistantCore/Engine/EAgentEngine.cs` — `GenerateAsync()` returns `Task<LLMDecision>`; stores `[reasoning]` in transcript
- `ECAssistantCore/Orchestrator.cs` — consumes `LLMDecision` directly (no `ParseLLMDecision`)
- `ECAssistantLLM/Engine/DecisionGrammar.cs` — GBNF template (stateless)
- `ECAssistantLLM/Engine/StructuredDecoder.cs` — envelope JSON → typed decision DTO + control char escaping
- `ECAssistantLLM/Models/DecisionEnvelope.cs` — immutable DTO
- `ECAssistantCore/Engine/StructuredDecisionAdapter.cs` — envelope JSON → `LLMDecision` (stateless, `ParseDecision()`)
- `ECAssistantCore/Engine/LLMDecision.cs` — decision DTO with `FromEnvelope()` factory
- `ECAssistantCore/Engine/EAgentEngine.cs` — `GenerateAsync()` returns `Task<LLMDecision>`
- `ECAssistantCore/Orchestrator.cs` — consumes `LLMDecision` directly (no `ParseLLMDecision`)

## KV Cache
- `SessionContext.RewindAsync()` restores both native KV cache (`_context.LoadState`) AND executor bookkeeping (`Executor.LoadState`) together — prevents `InvalidInputBatch` mismatch.

**Status:** v14.7 live. Tags removed. Reasoning stored in history. Early termination on envelope completion. All projects build clean (0 errors, 0 warnings). LDC enforcement PASSED.
**Added:** 2026-09-02