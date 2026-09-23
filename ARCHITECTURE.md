# ECAssistantLLM — Architecture (as-is)

**Updated:** 2026-09-23 · **Status:** ✅ 0 errors, 0 warnings | LDC enforcement PASSED (185 types)
**History:** git log — this file describes the CURRENT state only.
**Topical docs:** ARCHITECTURE-STRUCTURED-DECODING.md (decision grammar pipeline), ARCHITECTURE-BACKENDS.md (process backends)

## Overview

ECAssistantLLM is a standalone console app that wraps **LLamaSharp** and exposes an
OpenAI-compatible HTTP API with ECAssistant-specific extension endpoints. It is the
**"model server" half** of the ECAssistant split. ECAssistantCore talks to it over
HTTP/SSE and never touches LLamaSharp directly — this process owns the model, the
weights, the GPU, and the per-session KV caches.

- **Console app** (`Program.cs` top-level statements, `OutputType=Exe`)
- **HttpListener-based** — zero external HTTP framework; `System.Net.HttpListener` only
- **OpenAI-compatible**: `/v1/chat/completions`, `/v1/completions` (fully implemented), `/v1/embeddings`, `/v1/models`
- **Extension endpoints** (`/eca/*`): client lifecycle, per-session KV-cache control, runtime model load/unload, tokenization
- **Multi-client**: multiple Core instances share one server, each with isolated sessions
- **VRAM budget**: tracks estimated VRAM per session, refuses over-budget creation (503)
- **Serialized inference**: one inference at a time across all clients (FIFO)

## OOP Principles

- **Encapsulation:** Config and model DTOs are sealed classes. Config sections are mutable
  (deserialized JSON, `{ get; set; }`); API DTOs are mutable request/response carriers.
  Engine types hide all mutable state behind read-only properties.
- **No globals / mutable statics:** All runtime dependencies are injected via constructors.
  The only statics are *stateless* helpers: `SseStreamer` (stateless I/O utility), and
  factory methods `LlmServerConfig.Load/TryLoad` and `ChatCompletionChunk.Delta/Finish`.
  No static mutable state anywhere.
- **No cross-dependencies (layered):** `Server → Engine → Config`; `Models` and `Root` are
  leaf packages. Nothing depends upward.
- **Single responsibility:** One primary type per file. `Engine` types own one concern each.
- **Modular & mockable:** Behavior sits behind a single abstraction, `ILogger`, so logging
  can be swapped/mocked. The server is self-contained (no other internal interfaces).
- **Constructor injection throughout:** every Engine/Server type takes its dependencies in
  its constructor with `ArgumentNullException` guards.
- **`IDisposable` on every resource-owning type:** `ModelSlot`, `SessionContext`,
  `SessionRegistry`, `MultiModelHost`, `ClientManager`, `LlmHttpServer`.

## Project Structure

```
ECAssistantLLM/                 # 22 .cs files, ~2,537 LOC
├── Program.cs                  # Entry point: `[--port <N>] [path-to-llm-server.json]`, apply port override, wire components, run server, handle shutdown
├── ServerLogger.cs            # ILogger interface + LogLevel enum + ServerLogger impl (file + console)
├── ECAssistant.LLM.csproj      # net8.0 exe, LLamaSharp 0.27.0 + CPU/Cuda12/Vulkan backends, Microsoft.Extensions.Logging.Abstractions
├── llm-server.json             # Server config (models, ports, inference defaults, logging)
│
├── Config/                    # Config loading + section models (leaf package)
│    ├── LlmServerConfig.cs     # Root config: Load/TryLoad + Validate; static JsonOptions
│    └── Models/                # Config section models
│        ├── ServerSection.cs    # host, port, max_sessions, max_vram_mb + computed Prefix
│        ├── ModelConfig.cs      # id, path, gpu_layers, context_size, threads, batch_size, is_embedding, backend (auto|llamasharp|process)
│        ├── BackendsSection.cs  # backends_root, models_root, port_min/port_max (backend child processes)
│        ├── InferenceDefaults.cs# max_tokens, temperature, top_p, top_k, repeat_penalty
│        └── LoggingSection.cs   # level, file
│
├── Engine/Backends/            # Process backend (ternary/external models via child llama-server)
│    ├── TernaryModelDetector.cs # GGUF header sniff: ternary-packed (PTQ1_0/PQ2_0) → Process backend
│    ├── BackendSelector.cs      # ModelBackendKind per model: ternary → Process; explicit backend field overrides
│    ├── BackendPortAllocator.cs  # Random port from configurable range (default 20000-25000), skips used
│    ├── RuntimeManifest.cs / PlatformId.cs / PlatformRuntimeCatalog.cs / RuntimeLocator.cs  # pre-installed Prism llama.cpp runtimes (NEVER downloaded at runtime)
│    ├── IProcessModelHost.cs    # DI seam over the process host
│    ├── ProcessModelInstance.cs # One llama-server child process: spawn, health check, stop
│    ├── ProcessModelHost.cs     # Supervises child processes; lazy start, race-safe; stateless proxy endpoint
│    ├── ProcessSession.cs       # Transcript-backed session for process models (session parity, prefix cache)
│    ├── ProcessSessionRegistry.cs # Client-namespaced process sessions; mirrors SessionRegistry API
│    ├── ProcessPayloadFactory.cs  # Single source for child chat payloads: sampling parity with in-process (explicit defaults), grammar/thinking fields
│    └── ProcessStatelessClient.cs # Stateless (no-transcript) child inference; ThinkFilter/structured handled by router, in-process shapes
│
├── Engine/                    # Core engine (owns LLamaSharp types)
│    ├── MultiModelHost.cs       # Loads/unloads 2+ models; provides slots by ID; ModelInfo record
│    ├── ModelSlot.cs            # One model: LLamaWeights + ModelParams + optional LLamaEmbedder
│    ├── SessionRegistry.cs      # Thread-safe session CRUD, namespaced by clientId; SessionStatusInfo record
│    ├── SessionContext.cs       # Per-session InteractiveExecutor + KV cache: prefill/infer/rewind/save/reset
│    ├── InferenceScheduler.cs   # Serialized inference via SemaphoreSlim(1,1); nested InferenceReleaser (IAsyncDisposable)
│    ├── VramBudget.cs           # Estimated VRAM tracking + budget enforcement
│    ├── ClientManager.cs        # Client registration + explicit disconnect (no eviction/heartbeat — clients live until Disconnect); ClientRecord (internal) + ClientInfo record
│    ├── DecisionGrammar.cs      # v14 GBNF grammar — forces valid DecisionEnvelope JSON output at sampler level
│    └── StructuredDecoder.cs    # v14 Parses grammar output → DecisionEnvelope DTO; escapes raw control chars
│
├── Server/
│    ├── LlmHttpServer.cs        # HttpListener accept loop; dispatches each request to RequestRouter; catches JsonException → 400
│    ├── RequestRouter.cs        # Path/method routing + all endpoint handlers; v14.7 TryParseCompleteEnvelope() early termination
│    └── SseStreamer.cs          # Stateless helper: SSE stream (chat + completion), JSON read/write
│
└── Models/                    # API request/response DTOs (leaf package)
     ├── ChatCompletionRequest.cs# OpenAI chat request + ECAssistant session_id; ChatMessage
     ├── ChatCompletionChunk.cs  # SSE chunk + ChunkChoice + ChunkDelta; Delta()/Finish() factories
     ├── CompletionModels.cs     # OpenAI text completion: CompletionRequest/Response/Chunk/Choice (streaming + non-streaming)
     ├── EmbeddingModels.cs      # EmbeddingRequest / EmbeddingResponse / EmbeddingData
     ├── TokenizeModels.cs       # TokenizeRequest / TokenizeResponse
     └── ApiModels.cs            # ErrorResponse/ErrorDetail, SuccessResponse, client/session/model DTOs
```

## Dependency Flow

```
Program.cs  (composition root — wires everything by hand, no DI container)
   │
   ▼
Server/LlmHttpServer  ──►  Server/RequestRouter
   │   (accept loop,        (path/method dispatch + all handlers,
   │    per-request Task)   SSE via SseStreamer)
   │        │
   │        ▼
   │   Engine/MultiModelHost  ──►  Engine/ModelSlot  ──►  LLamaSharp
   │        │                     (LLamaWeights, ModelParams, LLamaEmbedder)
   │        ▼
   │   Engine/SessionRegistry ──► Engine/SessionContext ──► LLamaSharp
   │        │                    (InteractiveExecutor, KV cache, LLamaContext)
   │        ▼
   │   Engine/Backends/ProcessSessionRegistry ──► ProcessSession ──► HttpClient
   │        │                     (transcript-backed; child llama-server slot KV/prefix cache)
   │        ▼
   │   Engine/Backends/ProcessModelHost ──► ProcessModelInstance ──► llama-server child
   │        │                     (ternary/Bonsai; random port via BackendPortAllocator)
   │        ▼
   │   Engine/InferenceScheduler  (SemaphoreSlim gate — one inference at a time)
   │   Engine/VramBudget          (per-session VRAM accounting)
   │   Engine/ClientManager       (registration / explicit disconnect)
   ▼
Config/LlmServerConfig  (root config, validated once at startup)
Models/  (DTOs shared by Server + Config)
```

**LLamaSharp is the only external NuGet dependency** (plus `Microsoft.Extensions.Logging.Abstractions`
for `NullLogger<T>` used to silence LLamaSharp's own logging). The HTTP layer adds no framework —
`System.Net.HttpListener` + `System.Text.Json` only.

## Key Components

### Engine

| Component | Purpose |
|---|---|
| `MultiModelHost` | Loads all configured models at startup; runtime load/unload; resolves slots by ID, main slot, embedding slot; emits `ModelInfo` |
| `ModelSlot` | Wraps one model: `LLamaWeights` + `ModelParams` (+ `LLamaEmbedder` for embedding models); `Load`/`Unload`/`Dispose`; resolves model path across candidate dirs |
| `SessionRegistry` | Thread-safe (`ConcurrentDictionary`) session CRUD keyed by `{clientId}:{sessionId}`; enforces `MaxSessions`; builds `SessionContext`; emits `SessionStatusInfo` |
| `SessionContext` | Per-session `InteractiveExecutor` + `LLamaContext` (own KV cache); `PrefillAsync`, `InferAsync` (streaming), `SaveState`, `RewindAsync`, `Reset`; estimates token count + VRAM |
| `InferenceScheduler` | Serializes all inference via `SemaphoreSlim(1,1)` FIFO; `AcquireAsync` returns a disposable `InferenceReleaser` (nested, `IAsyncDisposable`) |
| `VramBudget` | Lock-guarded estimated-VRAM accounting; `TryReserve`/`Release`; `null` max = unlimited |
| `ClientManager` | Client registration (UUID id) + explicit disconnect (frees all client sessions). No heartbeat/eviction — clients live until Disconnect |
| `PromptCacheSession` | Persistent warm prompt-cache per model using only supported `SaveState`/`LoadState` snapshots on fresh executors. Reuse paths: growth (suffix decode) → stable-template checkpoint → cold. Runs only when `RequestRouter.EnableWarmPromptCache = true` |

### Engine/Backends (process backend — ternary/external models)

| Component | Purpose |
|---|---|
| `TernaryModelDetector` | Sniffs GGUF headers (PTQ1_0/PQ2_0 ternary packing); stateless utility |
| `BackendSelector` | Resolves `ModelBackendKind` per model: ternary → Process; explicit `backend` field overrides; default LlamaSharp |
| `BackendPortAllocator` | Random port from configurable range (default 20000–25000) for child llama-server processes; skips used; throws when exhausted |
| `RuntimeManifest` / `PlatformId` / `PlatformRuntimeCatalog` / `RuntimeLocator` | Pre-installed Prism llama.cpp runtimes per OS; the server NEVER downloads — missing pieces are hard errors pointing to the setup wizard |
| `IProcessModelHost` | DI seam: `EnsureStartedAsync(config)`, `EnsureStartedUrlAsync(modelId)`, `Instances`, `StopAllAsync` |
| `ProcessModelInstance` | One llama-server child: spawn, 120 s health wait, graceful stop, dispose; OpenAI-compatible endpoint on its port |
| `ProcessModelHost` | Supervises child processes; per-model lazy start with in-flight gate (race-safe); used for 1:1 stateless proxying |
| `ProcessSession` | Transcript-backed session for process models. Mirrors `SessionContext` contract: `PrefillAsync` (warms child's prefix cache with dummy user turn — Qwen templates reject system-only), `InferAsync` (streams content deltas; failed turns roll back appended messages), `SaveState`/`RewindAsync`/`Reset` (transcript snapshots under an IO lock). Efficiency comes from the child's slot KV/prefix cache; correctness never depends on it. `EstimatedVramMb = 0` — KV lives in the child |
| `ProcessSessionRegistry` | Client-namespaced process sessions; same API/contract as `SessionRegistry` (MaxSessions enforced, no VramBudget) |

**Client-facing parity:** `session_id` on a process model goes through `ProcessSessionRegistry` —
create/prefill/rewind/save-state/reset/status/destroy + streaming chat behave identically to
in-process KV sessions. Structured mode (grammar-enforced) stays LlamaSharp-only → clean 400.

### Server

| Component | Purpose |
|---|---|
| `LlmHttpServer` | `HttpListener` accept loop; spawns a `Task` per request → `RequestRouter`; owns `IDisposable` teardown order |
| `RequestRouter` | Routes by path+method to OpenAI and `/eca/*` handlers; validates `X-Client-Id`; builds prompts/`InferenceParams`; runs stateless inference for session-less requests |
| `SseStreamer` | Stateless helper: `StreamAsync` (chat SSE chunks + `[DONE]`), `StreamCompletionAsync` (completion SSE chunks), `WriteJsonAsync`, `ReadJsonAsync<T>` |

### Root / Config

| Component | Purpose |
|---|---|
| `Program.cs` | Composition root: parse `[--root <dir>] [--port <N>] [path-to-llm-server.json]`, root-only model path resolution (`{root}/{path}` or `{root}/models/{filename}`; absolute paths outside root → fail), `LlmServerConfig.TryLoad`, apply CLI port override (after load, before start, logged), build logger, wire Engine+Server, handle Ctrl+C / ProcessExit, `RunAsync`, dispose |
| `ILogger` / `LogLevel` / `ServerLogger` | Single logging abstraction (console + file, thread-safe via lock); the only internal interface |
| `LlmServerConfig` | Root config loader: `Load`/`TryLoad` + `Validate` (port range, ≥1 model, unique ids, non-empty id/path); shared `JsonOptions` |
| `ServerSection` / `ModelConfig` / `InferenceDefaults` / `LoggingSection` | JSON section models (mutable, deserialized) |
| `Models/*` | OpenAI-compatible + ECAssistant API request/response DTOs |

## API Surface

All requests that touch a session carry an `X-Client-Id` header. JSON is camelCase via `JsonPropertyName`.

### OpenAI-Compatible Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/chat/completions` | Chat completion, streaming (SSE) + non-streaming; routes to a session via `session_id` or runs stateless |
| POST | `/v1/completions` | Text completion, streaming (SSE) + non-streaming; routes to a session via `session_id` or runs stateless; supports `stop`, custom params |
| POST | `/v1/embeddings` | Generate embeddings via the embedding model's `LLamaEmbedder` |
| GET | `/v1/models` | List loaded models (OpenAI shape: `object:"list"`, `owned_by:"ecassistant"`, `loaded`, `is_embedding`) |

### ECAssistant Extension Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/eca/health` | Health: status, version, loaded models, session/client counts, uptime |
| POST | `/eca/clients` | Register a client → returns `client_id` (UUID) + server version |
| DELETE | `/eca/clients/{id}` | Disconnect client → frees all its sessions (in-process AND process-backed) |
| POST | `/eca/sessions` | Create a session (own KV cache); checks `MaxSessions` + VRAM budget (503 if over) |
| POST | `/eca/sessions/{id}/prefill` | Prefill a static prefix into the session's KV cache |
| POST | `/eca/sessions/{id}/rewind` | Rewind KV cache to the last saved state |
| POST | `/eca/sessions/{id}/save-state` | Snapshot current KV cache state |
| POST | `/eca/sessions/{id}/reset` | Discard + recreate the KV cache (caller must re-prefill). Process models: transcript reset |
| GET | `/eca/sessions/{id}/status` | Session status: model, prefilled, token count, context, VRAM, timestamps. Works for process sessions too (VRAM = 0) |
| DELETE | `/eca/sessions/{id}` | Destroy a session → frees KV cache + releases VRAM |
| GET | `/eca/models` | List all model slots with status (raw `ModelInfo` shape) |
| POST | `/eca/models/load` | Load a model at runtime (`id`, `path`, gpu_layers, context_size, threads, is_embedding) |
| POST | `/eca/models/unload` | Unload a model at runtime (`id`) |
| POST | `/eca/tokenize` | Tokenize text → token count + token IDs (via a temporary context) |

**Session-less inference:** a `/v1/chat/completions` with no `session_id` (or a session that
doesn't exist is a 404) runs through `StatelessInferAsync` on a fresh `StatelessExecutor` — no
KV cache, fresh each call.

## Multi-Client Design

- **Registration:** a Core instance calls `POST /eca/clients` → receives a UUID `client_id`.
  All subsequent requests send it in the `X-Client-Id` header.
- **Session namespacing:** sessions are keyed `{clientId}:{sessionId}` in a
  `ConcurrentDictionary`, so two clients can reuse the same `session_id` without collision.
- **Client lifetime (2026-09-23):** no heartbeat, no eviction. Clients stay registered until
  an explicit `DELETE /eca/clients/{id}` (Disconnect). Eviction was removed because idle or
  briefly-disconnected clients must never 401 mid-session.
- **VRAM budget:** `VramBudget` accumulates each session's `EstimatedVramMb` (approx
  `2 · layers · ctx · dim · sizeof(half)`). `POST /eca/sessions` calls `TryReserve`; on
  failure it rolls back the session and returns **503 `vram_exceeded`**. `null`
  `max_vram_mb` = unlimited.
- **Inference scheduling:** `InferenceScheduler` gates every inference behind a
  `SemaphoreSlim(1,1)` FIFO — **one model, one inference at a time**, even across clients.
  Awaiting requests contribute to `QueueDepth`.
- **Teardown cascade:** client disconnect / eviction → `SessionRegistry.DestroyClientSessions`
  disposes each `SessionContext` (frees its `LLamaContext`/KV cache) and releases its VRAM.

## Config (`llm-server.json`)

Loaded once at startup by `LlmServerConfig.TryLoad` (falls back to CWD if not next to the
executable). `Validate` enforces: port 1–65535, ≥1 model, unique non-empty model ids/paths.

```jsonc
{
  "server": {
    "host": "localhost",
    "port": 8420,
    "max_sessions": 8,               // hard cap on concurrent SessionContexts
    "max_vram_mb": null,            // null = unlimited; else reject over-budget sessions (503)
    "heartbeat_timeout_sec": 90,     // eviction timer interval + staleness cutoff
    "heartbeat_interval_sec": 30     // advisory: how often clients should heartbeat
  },
  "models": [
    { "id": "main",       "path": "models/qwen3-8b-q4_k_m.gguf",
      "gpu_layers": 99, "context_size": 32768, "threads": -1,
      "is_embedding": false },
    { "id": "embeddings", "path": "models/all-MiniLM-L6-v2-q4_k_m.gguf",
      "gpu_layers": 0, "context_size": 2048, "threads": -1,
      "is_embedding": true }
  ],
  "inference": {
    "max_tokens": 512, "temperature": 0.3, "top_p": 0.95,
    "top_k": 40, "repeat_penalty": 1.1
  },
  "backends": {
    "backends_root": "backends",       // pre-installed Prism llama.cpp runtimes
    "models_root": "models",
    "port_min": 20000,                 // random port range for child llama-server processes
    "port_max": 25000
  },
  "logging": { "level": "info", "file": "ecassistant-llm.log" }
}
```

Section semantics:

- **`server`** — binding (`host`/`port` → computed `Prefix`), session cap, VRAM budget, heartbeat cadence. `gpu_layers`, `threads`, `batch_size` are **server-side** concerns (not in Core config). `--port <N>` on the CLI overrides `server.port` after config load.
- **`models`** — one `ModelConfig` each. `gpu_layers` is clamped to `[0,100]`; `threads: -1` → auto; `batch_size: 0` → LLamaSharp default. First non-embedding model = **main**; first embedding model = **embeddings**. Embedding models use `pooling_type` (`mean`/`cls`/`last`/`none`, default `mean`). `mmproj_path` (optional) enables vision: MTMD projector loaded lazily from the mmproj GGUF on first vision request (`SupportsVision` = true). `backend`: `"auto"` (default — ternary-detected GGUF → process), `"llamasharp"`, or `"process"` (external child llama-server). Process models may also carry `download_url`/`download_sha256` for the WIZARD to install weights (the server itself never downloads).
- **`backends`** — process-backend home (`backends_root`, `models_root`) and the random port range (`port_min`/`port_max`, defaults 20000–25000) for child llama-server processes.

## Vision (MTMD / mmproj)

- **Request format:** OpenAI multimodal content parts — `"content": [{"type":"text","text":...},{"type":"image_url","image_url":{"url":"data:image/png;base64,..."}}]`. Parsed by `ChatMessageContentConverter` (on the `messages` array): text parts joined, each image part becomes an `<__image__>` marker in `Content` + decoded bytes in `Images`. Only base64 data URIs are accepted (no URL fetch).
- **Model wiring:** `ModelSlot.Mmproj` lazily loads `MtmdWeights.LoadFromFile(mmproj_path, weights)` on first use. Sessions created via `SessionRegistry.CreateSession` attach the slot's projector; `SessionContext.Reset` preserves it.
- **Warm sessions:** `SessionContext.InferAsync(prompt, ct, images)` — under `_ioLock`, media is queued into the projector FIFO before inference (one marker per image, in order), cleared after.
- **Stateless:** `StatelessVisionInferAsync` builds a fresh context + MTMD executor per request. Vision requests always bypass the warm prompt cache (the projector's media queue is global per model).
- **Capability:** `/eca/health` returns `"vision": true` when any loaded model has vision. Vision requests to non-vision models → 400.

- **`inference`** — default sampling params, overridable per-request via `temperature`/`top_p`/`top_k`/`max_tokens`/`repeat_penalty` on the chat request.
- **`logging`** — `level` (`debug`/`info`/`warn`/`error`) + log file path (resolved relative to the config dir).

## Lifecycle

1. **Startup** — `Program.cs` parses `[--root <dir>] [--port <N>] [path-to-llm-server.json]`, resolves the root (arg required when launched by Core; standalone falls back to CWD), resolves the config path (explicit arg → `{root}/llm-server.json`; default generated ONLY for standalone runs with no args), `LlmServerConfig.TryLoad` + `Validate`, applies the `--port` override to `server.port` (after load, before start, logged), builds `ServerLogger`, constructs `MultiModelHost`/`InferenceScheduler`/`VramBudget` → `LoadAll()` (all model paths resolved strictly inside `--root`; absolute paths outside root → fail), then `SessionRegistry`, `ClientManager`, `LlmHttpServer`.
2. **Model loading** — `MultiModelHost.LoadAll()` creates a `ModelSlot` per config and calls `Load()` (loads `LLamaWeights`, and for embedding models a `LLamaEmbedder` + a probe call to determine `EmbeddingDim`). A load failure is fatal at startup.
3. **Server run** — `LlmHttpServer.RunAsync` starts the listener and accepts a `Task` per request; each is dispatched to `RequestRouter.RouteAsync`.
4. **Client connect** — Core `POST /eca/clients` → UUID `client_id`; client starts a heartbeat timer.
5. **Session creation** — Core `POST /eca/sessions` with `session_id` (+ optional `model_id`) → `SessionRegistry.CreateSession` builds a `SessionContext` (own `LLamaContext`/KV cache), guarded by `MaxSessions` and `VramBudget.TryReserve` (503 on over-budget).
6. **Prefill** — `POST /eca/sessions/{id}/prefill` caches the static prefix (system prompt + tools) into the KV cache; 120s internal timeout.
7. **Inference** — `POST /v1/chat/completions` with `session_id` acquires the scheduler gate and streams tokens via SSE (or returns a full JSON body when `stream:false`). No `session_id` → stateless executor.
8. **Turn management** — after each turn Core may `POST /eca/sessions/{id}/save-state` then `/rewind` to restore a clean prefilled prefix.
9. **Heartbeat / eviction** — periodic heartbeats keep the client alive; a stale client is evicted by the `Timer`, freeing its sessions.
10. **Shutdown** — Ctrl+C / process exit cancels the `CancellationTokenSource`; `LlmHttpServer.Dispose` stops the listener and disposes `ClientManager` → `SessionRegistry` → `MultiModelHost` (frees all KV caches + weights).

## Key Constraints

- **One type per file**, one concern per type. Nested helper types stay with their owner
  (`InferenceReleaser` inside `InferenceScheduler`, `ClientRecord`/`ClientInfo` with `ClientManager`).
- **Constructor injection throughout** with `ArgumentNullException` guards; no static mutable
  state. Allowed statics are stateless only (`SseStreamer`, `LlmServerConfig.Load/TryLoad`,
  `ChatCompletionChunk.Delta/Finish`).
- **Layered dependencies:** `Server → Engine → Config`; `Models`/`Root` are leaves. No upward deps.
- **Threading model:** the accept loop is single-threaded but each request runs on its own
  `Task`; shared state is `ConcurrentDictionary` (`SessionRegistry`, `ClientManager`) or
  lock-guarded (`VramBudget`). All **inference is serialized** by `InferenceScheduler`
  (`SemaphoreSlim(1,1)`) — a single model, single active inference.
- **Resource discipline:** `IDisposable` on every resource-owning type; disposal order in
  `LlmHttpServer.Dispose` is clients → sessions → models.
- **GPU/threads/batch_size are server-side** — configured in `llm-server.json`, not in Core.
- **Core-owned config (v12.11):** when launched by Core, the config path is always passed explicitly and the file is guaranteed by Core's `ServerConfigWriter.EnsureServerConfig` (wizard selections, root-contained paths). Default generation applies only to standalone runs without a config argument. Relative model paths resolve only as `{root}/{path}` or `{root}/models/{filename}`.
- **No HTTP framework** — `System.Net.HttpListener` + `System.Text.Json` only; the sole
  external dependency is LLamaSharp.
- **Error hardening:** `LlmHttpServer` catches `JsonException` → 400 (invalid JSON body),
  generic exceptions → 500. `RequestRouter` validates empty prompts/text/input → 400,
  whitespace client names → 400 (`IsNullOrWhiteSpace`), missing sessions → 404.
- **Integration tests:** 64 tests covering all endpoints (health, clients, sessions, KV cache,
  chat completions, text completions, embeddings, models, tokenize, error handling, routing).
- **Warm prompt cache (`EnableWarmPromptCache`) is OFF by default.** Safe and verified on
  plain-attention models (~9× speedup on Qwen3-8B). On hybrid Gated-DeltaNet models
  (Qwen3.x-35B-A3B) restored snapshots progressively corrupt output: an upstream llama.cpp bug —
  `copy_cell` passes a byte count to `ggml_view_1d` (expects elements) during recurrent-state
  checkpoint/restore (PR #20700, closed unmerged; see issues #21681/#22384). Revisit when the
  fix lands upstream; until then stateless calls use `StatelessExecutor` cold path.

