# ECAssistantLLM — Architecture (as-is)

**Updated:** 2026-09-25 · **Status:** ✅ 0 errors, 0 warnings | LLamaSharp REMOVED — powered by ECAssistantInference
**Addendum 2026-09-25 (LLamaSharp → ECAssistantInference migration):** ALL LLamaSharp dependencies removed. The server now uses ECAssistantInference (native C/C++ engine linking llama.cpp directly, P/Invoked from C#). Key type mapping: `LLamaWeights`→`IInferenceModel`, `LLamaContext`→`IInferenceContext`, `BatchedExecutor`→`IConversationPool`, `Conversation`→`IConversation`, `InferenceParams`/`DefaultSamplingPipeline`→`SamplingConfig`, `LLamaTemplate`→`eci_apply_chat_template` (native), `Grammar`→`IGrammar` (GBNF via `llama_sampler_init_grammar`), `InteractiveExecutor`→`IStandardExecutor`, `StatelessExecutor`→manual context+executor per request, `MtmdWeights`→`IVisionEncoder`. Three gaps closed: (1) Grammar threaded through `SessionContext.InferAsync(grammarStr, grammarRoot)`→`SampleWithGrammar()`, (2) Vision on standard executor via `eci_executor_prompt_with_images`, (3) Chat template via `model.ApplyChatTemplate()` (model-native, not hardcoded ChatML). Batch mode grammar not wired (standard path only — structured/tools use sessions). 107/107 non-model tests passing. Total test count across both repos: 201/201.
**Addendum 2026-09-25 (batch_context_size REMOVED — Emre):** The batch KV pool inherits each model's `context_size` directly — ONE knob, matching Ollama's single `num_ctx` philosophy.
**Addendum 2026-09-25 (batch concurrency — CURRENT MODEL):** Requests enqueue ops into thread-safe FIFO `BatchOpBuffer`. Each cycle, serialized by `_cycleGate`: dispose graveyard → drain ops FIFO → ONE `InferAll()` when a prompt flushed. Per-session `SemaphoreSlim` request gate serializes concurrent requests to the same session. Disposal deferred to cycle gate.
**Addendum 2026-09-25 (continuous batching, opt-in):** `continuous_batching` config flag (default false) activates `IConversationPool` for ALL inference. When off: zero new code executed, 100% current behavior. When on: 4 Engine types (`BatchedExecutorHost`, `BatchInferenceCoordinator`, `BatchSession`, `BatchSessionRegistry`) provide batched decode — multiple conversations in ONE `llama_decode` call via shared `IInferenceContext`.
**Addendum 2026-09-25 (audit fixes + vision serialization):** Cycle gate serializes mutations+flush+Infer. PrefillAsync checks InferResult. EvaluateAsync decodes between samples. ClientManager.Disconnect destroys batch sessions. Vision media buffered per-session, loaded by coordinator inside cycle gate.
**Isolation hardening:** Overflow safety net skips busy sessions. Retire-mid-stream guard aborts on `_retired || _conversationDisposed`. Graceful shutdown acquires cycle gate before disposing executor.
**History:** git log — this file describes the CURRENT state only.
**Topical docs:** ARCHITECTURE-STRUCTURED-DECODING.md (decision grammar pipeline), ARCHITECTURE-BACKENDS.md (process backends)

## Overview

ECAssistantLLM is a standalone console app that wraps **ECAssistantInference** (native C/C++
llama.cpp engine) and exposes an OpenAI-compatible HTTP API with ECAssistant-specific
extension endpoints. It is the **"model server" half** of the ECAssistant split.
ECAssistantCore talks to it over HTTP/SSE and never touches native inference directly —
this process owns the model, the weights, the GPU, and the per-session KV caches.

- **Console app** (`Program.cs` top-level statements, `OutputType=Exe`)
- **HttpListener-based** — zero external HTTP framework; `System.Net.HttpListener` only
- **OpenAI-compatible**: `/v1/chat/completions`, `/v1/completions` (fully implemented), `/v1/embeddings`, `/v1/models`
- **Extension endpoints** (`/eca/*`): client lifecycle, per-session KV-cache control, runtime model load/unload, tokenization
- **Multi-client**: multiple Core instances share one server, each with isolated sessions
- **VRAM budget**: tracks estimated VRAM per session, refuses over-budget creation (503)
- **Serialized inference**: one inference at a time across all clients (FIFO)

## OOP Principles

- **Encapsulation:** Config and model DTOs are sealed classes. Engine types hide all mutable state behind read-only properties.
- **No globals / mutable statics:** All runtime dependencies injected via constructors. Statics are stateless helpers only.
- **No cross-dependencies (layered):** `Server → Engine → Config`; `Models` and `Root` are leaf packages.
- **Single responsibility:** One primary type per file.
- **Constructor injection throughout** with `ArgumentNullException` guards.
- **`IDisposable` on every resource-owning type.**

## Project Structure

```
ECAssistantLLM/                 # ~22 .cs files
├── Program.cs                  # Entry point: [--root <dir>] [--port <N>] [config.json]
├── ServerLogger.cs            # ILogger interface + LogLevel enum + ServerLogger impl
├── ECAssistant.LLM.csproj      # net8.0 exe, ECAssistantInference + Microsoft.Extensions.Logging.Abstractions
├── llm-server.json             # Server config
│
├── Config/                    # Config loading + section models
├── Engine/Backends/            # Process backend (ternary/external models)
├── Engine/                    # Core engine (owns ECAssistantInference types)
│    ├── MultiModelHost.cs       # Loads/unloads models; provides slots by ID
│    ├── ModelSlot.cs            # One model: IInferenceModel + config + IVisionEncoder
│    ├── SessionRegistry.cs      # Thread-safe session CRUD, namespaced by clientId
│    ├── SessionContext.cs       # Per-session IStandardExecutor + KV cache
│    ├── BatchedExecutorHost.cs  # Owns BatchInferenceCoordinator per model (batch mode)
│    ├── BatchInferenceCoordinator.cs # Coordinates batched decode across sessions
│    ├── BatchSession.cs         # Wraps IConversation on shared pool
│    ├── InferenceScheduler.cs   # Serialized inference via SemaphoreSlim(1,1)
│    ├── VramBudget.cs           # Estimated VRAM tracking
│    ├── ClientManager.cs        # Client registration + disconnect
│    ├── DecisionGrammar.cs      # GBNF grammar for structured output
│    └── StructuredDecoder.cs    # Parses grammar output → DecisionEnvelope
├── Server/
│    ├── LlmHttpServer.cs        # HttpListener accept loop
│    ├── RequestRouter.cs        # Path/method routing + all endpoint handlers
│    └── SseStreamer.cs          # SSE stream helper
└── Models/                    # API request/response DTOs
```

## Dependency Flow

```
Program.cs  (composition root)
   │
   ▼
Server/LlmHttpServer  ──►  Server/RequestRouter
   │                        (path/method dispatch, SSE, prompt building)
   │        │
   │        ▼
   │   Engine/MultiModelHost  ──►  Engine/ModelSlot  ──►  ECAssistantInference
   │        │                     (IInferenceModel, ModelConfig, IVisionEncoder)
   │        ▼
   │   Engine/SessionRegistry ──► Engine/SessionContext ──►  ECAssistantInference
   │        │                    (IStandardExecutor, IInferenceContext, KV cache)
   │        ▼
   │   Engine/BatchedExecutorHost ──► BatchInferenceCoordinator ──► IConversationPool
   │        │                                             ──► IConversation (batched)
   │        ▼
   │   Engine/InferenceScheduler  (SemaphoreSlim gate)
   │   Engine/VramBudget          (per-session VRAM accounting)
   │   Engine/ClientManager       (registration / disconnect)
   ▼
Config/LlmServerConfig  (root config)
Models/  (DTOs)
```

**ECAssistantInference is the only external dependency** (plus `Microsoft.Extensions.Logging.Abstractions`).

## Inference Engine Mapping

| ECAssistantLLM Type | ECAssistantInference Interface | Purpose |
|---|---|---|
| `ModelSlot.Model` | `IInferenceModel` | Loaded GGUF model |
| `SessionContext._context` | `IInferenceContext` | Context with KV cache |
| `SessionContext._executor` | `IStandardExecutor` | Standard prompt→infer→sample loop |
| `BatchInferenceCoordinator._pool` | `IConversationPool` | Pre-allocated conversation pool |
| `BatchSession._conversation` | `IConversation` | Leased conversation (seq_id) |
| `ModelSlot.Vision` | `IVisionEncoder` | mmproj projector for vision |
| `SessionContext._savedState` | `IInferenceState` | KV snapshot for rewind |
| `SamplingConfig` | `SamplingConfig` | Temperature, top-k, top-p, penalties |
| Grammar (GBNF) | `IGrammar` | `llama_sampler_init_grammar` (persistent chain, grammar-first, prompt tokens accepted) |
| Tool calls | `SampleWithGrammar()` | Grammar-constrained sampling + early-stop JSON parsing fallback |
| Chat template | `ApplyChatTemplate()` | `llama_chat_apply_template` (model-native) |

## Key Components

### Engine

| Component | Purpose |
|---|---|
| `MultiModelHost` | Loads all configured models at startup; runtime load/unload; resolves slots by ID |
| `ModelSlot` | Wraps one model: `IInferenceModel` + config + `IVisionEncoder`; `Load`/`Unload`/`Dispose` |
| `SessionRegistry` | Thread-safe session CRUD keyed by `{clientId}:{sessionId}`; enforces `MaxSessions` + VRAM |
| `SessionContext` | Per-session `IStandardExecutor` + `IInferenceContext` (own KV cache); `PrefillAsync`, `InferAsync` (streaming, grammar, vision), `SaveState`, `RewindAsync`, `Reset`, `EvaluateAsync` |
| `InferenceScheduler` | Serializes all inference via `SemaphoreSlim(1,1)` FIFO |
| `VramBudget` | Lock-guarded estimated-VRAM accounting |
| `ClientManager` | Client registration + explicit disconnect |

### Batch Engine (opt-in via `continuous_batching`)

| Component | Purpose |
|---|---|
| `BatchedExecutorHost` | Owns `BatchInferenceCoordinator` per model; creates `IInferenceContext` + `IConversationPool` |
| `BatchInferenceCoordinator` | Coordinates `InferAll()` across sessions; cycle gate serializes; graveyard disposal |
| `BatchSession` | Wraps `IConversation`; FIFO op buffer; per-session request gate; deferred disposal |
| `BatchSessionRegistry` | Client-namespaced batch sessions; mirrors `SessionRegistry` API |

### Server

| Component | Purpose |
|---|---|
| `LlmHttpServer` | `HttpListener` accept loop; per-request `Task` → `RequestRouter` |
| `RequestRouter` | Routes by path+method; builds prompts via `ApplyChatTemplate`; creates `SamplingConfig`; grammar via `SampleWithGrammar`; stateless inference via manual context+executor |
| `SseStreamer` | SSE stream helper (chat + completion), JSON read/write |

## API Surface

All requests that touch a session carry `X-Client-Id`. JSON is camelCase.

### OpenAI-Compatible Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/chat/completions` | Chat completion (streaming + non-streaming); session via `session_id` or stateless |
| POST | `/v1/completions` | Text completion (streaming + non-streaming) |
| POST | `/v1/embeddings` | Embeddings via `IInferenceModel.GetEmbeddings()` |
| GET | `/v1/models` | List loaded models |

### ECAssistant Extension Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/eca/health` | Health: status, models, sessions, uptime |
| POST | `/eca/clients` | Register client → UUID |
| DELETE | `/eca/clients/{id}` | Disconnect client → frees sessions |
| POST | `/eca/sessions` | Create session (own KV cache) |
| POST | `/eca/sessions/{id}/prefill` | Prefill static prefix into KV |
| POST | `/eca/sessions/{id}/rewind` | Rewind to saved state |
| POST | `/eca/sessions/{id}/save-state` | Snapshot KV cache |
| POST | `/eca/sessions/{id}/reset` | Discard + recreate KV |
| POST | `/eca/sessions/{id}/evaluate` | Prompt-only feed (no conversation turn) |
| GET | `/eca/sessions/{id}/status` | Session status (tokens, headroom, has_saved_state) |
| DELETE | `/eca/sessions/{id}` | Destroy session → free KV |
| GET | `/eca/models` | List model slots |
| POST | `/eca/models/load` | Load model at runtime |
| POST | `/eca/models/unload` | Unload model |
| POST | `/eca/tokenize` | Tokenize text → token IDs |

## Config (`llm-server.json`)

```jsonc
{
  "server": {
    "host": "localhost", "port": 8420,
    "max_sessions": 8, "max_vram_mb": null,
    "continuous_batching": false
  },
  "models": [
    { "id": "main", "path": "models/qwen3-8b-q4_k_m.gguf",
      "gpu_layers": 99, "context_size": 32768, "threads": -1,
      "flash_attn": true, "kv_cache": "f16",
      "is_embedding": false }
  ],
  "inference": {
    "max_tokens": 512, "temperature": 0.3, "top_p": 0.95,
    "top_k": 40, "repeat_penalty": 1.1
  },
  "logging": { "level": "info", "file": "ecassistant-llm.log" }
}
```

## Vision (MTMD / mmproj)

- **Model wiring:** `ModelSlot.Vision` lazily loads `IVisionEncoder` via `IInferenceModel.LoadVisionEncoder(mmprojPath)` on first use.
- **Warm sessions:** `SessionContext.InferAsync(prompt, ct, images)` → `IStandardExecutor.PromptWithImages()` (native `eci_executor_prompt_with_images`).
- **Batch mode:** `IConversation.PromptWithImages()` (native `eci_conversation_prompt_with_images`).
- **Stateless:** `StatelessVisionInferAsync` creates fresh context + executor per request, uses `PromptWithImages()`.
- **Chat template:** `BuildPromptFromMessages` calls `model.ApplyChatTemplate(null, messages, addAssistant: true)` → native `llama_chat_apply_template` (Qwen, Llama, ChatML, etc. — model-native, not hardcoded).
- **Grammar:** `SessionContext.InferAsync(grammarStr, grammarRoot)` creates `IGrammar` internally → `SampleWithGrammar()` in the generation loop. RequestRouter passes `DecisionGrammar` / `ToolCallGrammar` / `req.Grammar`.

## Lifecycle

1. **Startup** — parse args, load config, build logger, wire Engine+Server, `LoadAll()`, run.
2. **Model loading** — `MultiModelHost.LoadAll()` → `ModelSlot.Load()` → `NativeInferenceModel.Load(config)`.
3. **Server run** — `LlmHttpServer.RunAsync` accepts Task per request → `RequestRouter`.
4. **Client connect** — `POST /eca/clients` → UUID.
5. **Session creation** — `POST /eca/sessions` → `SessionRegistry.CreateSession` → `SessionContext` (own `IInferenceContext` + `IStandardExecutor`).
6. **Prefill** — `POST /eca/sessions/{id}/prefill` caches static prefix.
7. **Inference** — `POST /v1/chat/completions` with `session_id` → `SessionContext.InferAsync` (grammar, vision, anti-prompts, headroom clamp). No `session_id` → stateless.
8. **Shutdown** — Ctrl+C → dispose clients → sessions → models.

## Known Issues

### Grammar sampler crash (upstream llama.cpp bug)
- **Affected:** 3 tests requiring grammar-constrained tool call generation
- **Root cause:** `llama_grammar_accept_chr` in `src/llama-grammar.cpp:1028` drops empty stacks when a multi-character token (e.g. Qwen token `[{`) spans grammar rules with optional whitespace. All stacks become empty → "Unexpected empty grammar stack" exception.
- **Workaround:** Try/catch on `llama_sampler_accept`; falls back to unconstrained sampling → 422 on tool call JSON parsing.
- **Fix needed:** Patch llama.cpp or update to a version with fixed grammar sampler.

## Key Constraints

- **One type per file**, one concern per type.
- **Constructor injection throughout** with `ArgumentNullException` guards.
- **Layered dependencies:** `Server → Engine → Config`; `Models`/`Root` are leaves.
- **Threading:** accept loop single-threaded, each request on its own `Task`; shared state is `ConcurrentDictionary` or lock-guarded. All inference serialized by `InferenceScheduler`.
- **Resource discipline:** `IDisposable` on every resource-owning type; disposal order: clients → sessions → models.
- **No HTTP framework** — `System.Net.HttpListener` + `System.Text.Json` only.
- **External dependency:** ECAssistantInference (native C/C++ llama.cpp engine, P/Invoked from C#).

## Test Coverage

- **ECAssistantInference C++:** 43/43 (14 basic + 29 stress)
- **ECAssistantInference C#:** 51/51
- **ECAssistantLLM non-model:** 107/107 (config, backends, buffer, ThinkFilter, EnvelopeSalvager)
- **ECAssistantLLM non-model:** 310/313 (3 grammar-constrained tool call tests — upstream llama.cpp grammar sampler bug with multi-char tokens)
- **Total: 204/207** (3 failures: upstream llama.cpp `llama_grammar_accept` crash on multi-character tokens spanning grammar rules)
