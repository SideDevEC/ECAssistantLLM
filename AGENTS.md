# AGENTS.md — ECAssistantLLM (AI-consumable)

Compact orientation for AI agents working in this repo. Humans: read README.md (+ ARCHITECTURE-STRUCTURED-DECODING.md, ARCHITECTURE-BACKENDS.md).

## Identity
- **Package:** `ECAssistant.LLM.Server` v15.0.0 · net8.0 exe · namespace `ECAssistant.LLM`
- **Purpose:** Self-contained, OpenAI-compatible local LLM server over LLamaSharp (llama.cpp). Also a standalone product — any OpenAI-compatible client works.
- **Boundary (hard rule):** No ECAssistantCore knowledge. The server is downstream of nothing; clients come to it over HTTP.

## Fast orientation
- 183 types / 27 edges — start with `API-INDEX.md` (~2.6K tokens) + `RELATIONSHIP-GRAPH.md`.
- Topical deep-dives: `ARCHITECTURE-STRUCTURED-DECODING.md` (GBNF pipeline), `ARCHITECTURE-BACKENDS.md` (process backends).

## Key components
| Type | Responsibility |
|---|---|
| `LlmHttpServer` | HTTP listener, routing, SSE streaming |
| `RequestRouter` | OpenAI-compatible + `/eca/*` extension endpoints |
| `MultiModelHost` | Loaded models, GPU layers, VRAM budget |
| `InferenceScheduler` | Queues/dispatches inference requests |
| `SessionRegistry` / `ProcessSessionRegistry` | KV-cache sessions (local models) / transcript-backed sessions (process models) — same client-facing behavior |
| `ClientManager` | Client registration, explicit disconnect (no heartbeat/eviction) |
| `StructuredDecoder` | GBNF grammar-constrained JSON decoding; auto-grammar from OpenAI `tools` JSON Schema |
| `ProcessModelHost` | Child llama-server supervision for ternary/external models (Bonsai) |
| `VramBudget` | GPU memory budget across models |

## API surface
- OpenAI-compatible: `POST /v1/chat/completions` (stream+non-stream) · `POST /v1/completions` · `POST /v1/embeddings` · `GET /v1/models` · `GET /health`
- ECA extensions: `POST /eca/clients` (register → `X-Client-Id`) · `DELETE /eca/clients/{id}` · `POST/DELETE /eca/sessions[/{id}]` · `POST /eca/tokenize` · `POST /eca/shutdown`
- All ECA-ecosystem calls require the `X-Client-Id` header (from client registration).

## Run & configure
```bash
dotnet run -- --root ~/ECALLM [--port 48217] [path/to/llm-server.json]
```
Config `~/ECALLM/llm-server.json`: `server.{host,port,shutdown_on_last_client}` · `models[]` (`id`, `path` RELATIVE to root, `gpu_layers`, `context_size`, `is_embedding`, `mmproj_path` for vision) · `inference` · `logging`.
- Server refuses model paths OUTSIDE `--root` (security). Vision models ALWAYS list mmproj.

## Build & test
```bash
dotnet build                      # 0 warnings expected
dotnet test                       # Tests/ — server lifecycle, chat, KV cache, security
# ⚠ leftover servers hold the port: pkill -f ECAssistant.LLM.dll before reruns
```

## Distribution
- Ships as NuGet content package: consuming projects get `server/` staged into build output; the ECAssistant wizard copies it to `~/ECALLM/server/`.
- Version LOCKSTEP with all ECAssistant packages (15.0.0). `ServerInstallCoordinator.RequiredServerVersion` in Core pins this — keep in sync.
- Publish: tag `llm-server-v15.0.0` → CI → nuget.org + GitHub Packages + Release. NEVER tag without Emre's "ship it".

## Gotchas agents have hit before
- TruncateAndReprefill throws on models without KV memory-shift (Qwen3.5) — overflow handling is app-layer (reset+refeed); set TruncateAndReprefill on ALL 4 InferenceParams factories.
- Structured-decode failures → HTTP 422 → client falls back to raw text streaming (envelope JSON visible). Debug server version + CreateSession status codes first.
- Server without registered client: `/v1/*` returns `invalid_client` — register via `/eca/clients` first.
