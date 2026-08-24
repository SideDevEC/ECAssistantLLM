# ECAssistantLLM — Architecture

**Updated:** 2026-08-24
**Status:** ✅ Build clean, net8.0, LLamaSharp 0.27.0

## Overview

ECAssistantLLM is a standalone console app that wraps **LLamaSharp** and exposes an
OpenAI-compatible HTTP API with ECAssistant-specific extension endpoints. It is the
**"model server" half** of the ECAssistant split. ECAssistantCore talks to it over
HTTP/SSE and never touches LLamaSharp directly — this process owns the model, the
weights, the GPU, and the per-session KV caches.

- **Console app** (`Program.cs` top-level statements, `OutputType=Exe`)
- **HttpListener-based** — zero external HTTP framework; `System.Net.HttpListener` only
- **OpenAI-compatible**: `/v1/chat/completions`, `/v1/completions` (501 stub), `/v1/embeddings`, `/v1/models`
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
├── Program.cs                  # Entry point: resolve+load config, wire components, run server, handle shutdown
├── ServerLogger.cs            # ILogger interface + LogLevel enum + ServerLogger impl (file + console)
├── ECAssistant.LLM.csproj      # net8.0 exe, LLamaSharp 0.27.0 + CPU/Cuda12/Vulkan backends, Microsoft.Extensions.Logging.Abstractions
├── llm-server.json             # Server config (models, ports, inference defaults, logging)
│
├── Config/                    # Config loading + section models (leaf package)
│    ├── LlmServerConfig.cs     # Root config: Load/TryLoad + Validate; static JsonOptions
│    └── Models/                # Config section models
│        ├── ServerSection.cs    # host, port, max_sessions, max_vram_mb, heartbeat_* + computed Prefix
│        ├── ModelConfig.cs      # id, path, gpu_layers, context_size, threads, batch_size, is_embedding, pooling_type
│        ├── InferenceDefaults.cs# max_tokens, temperature, top_p, top_k, repeat_penalty
│        └── LoggingSection.cs   # level, file
│
├── Engine/                    # Core engine (owns LLamaSharp types)
│    ├── MultiModelHost.cs       # Loads/unloads 2+ models; provides slots by ID; ModelInfo record
│    ├── ModelSlot.cs            # One model: LLamaWeights + ModelParams + optional LLamaEmbedder
│    ├── SessionRegistry.cs      # Thread-safe session CRUD, namespaced by clientId; SessionStatusInfo record
│    ├── SessionContext.cs       # Per-session InteractiveExecutor + KV cache: prefill/infer/rewind/save/reset
│    ├── InferenceScheduler.cs   # Serialized inference via SemaphoreSlim(1,1); nested InferenceReleaser (IAsyncDisposable)
│    ├── VramBudget.cs           # Estimated VRAM tracking + budget enforcement
│    └── ClientManager.cs        # Client registration, heartbeat, eviction; ClientRecord (internal) + ClientInfo record
│
├── Server/                    # HTTP layer
│    ├── LlmHttpServer.cs        # HttpListener accept loop; dispatches each request to RequestRouter
│    ├── RequestRouter.cs        # Path/method routing + all endpoint handlers (OpenAI + ECAssistant)
│    └── SseStreamer.cs          # Stateless helper: SSE stream, JSON read/write
│
└── Models/                    # API request/response DTOs (leaf package, ~209 LOC)
     ├── ChatCompletionRequest.cs# OpenAI chat request + ECAssistant session_id; ChatMessage
     ├── ChatCompletionChunk.cs  # SSE chunk + ChunkChoice + ChunkDelta; Delta()/Finish() factories
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
   │   Engine/InferenceScheduler  (SemaphoreSlim gate — one inference at a time)
   │   Engine/VramBudget          (per-session VRAM accounting)
   │   Engine/ClientManager       (registration / heartbeat / eviction)
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
| `ClientManager` | Client registration (UUID id), heartbeat recording, disconnect (frees all client sessions), and a `Timer`-driven eviction of stale clients |

### Server

| Component | Purpose |
|---|---|
| `LlmHttpServer` | `HttpListener` accept loop; spawns a `Task` per request → `RequestRouter`; owns `IDisposable` teardown order |
| `RequestRouter` | Routes by path+method to OpenAI and `/eca/*` handlers; validates `X-Client-Id`; builds prompts/`InferenceParams`; runs stateless inference for session-less requests |
| `SseStreamer` | Stateless helper: `StreamAsync` (SSE chunks + `[DONE]`), `WriteJsonAsync`, `WriteTextAsync`, `ReadJsonAsync<T>` |

### Root / Config

| Component | Purpose |
|---|---|
| `Program.cs` | Composition root: resolve config path, `LlmServerConfig.TryLoad`, build logger, wire Engine+Server, handle Ctrl+C / ProcessExit, `RunAsync`, dispose |
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
| POST | `/v1/completions` | Text completion — **501 Not Implemented** (stub) |
| POST | `/v1/embeddings` | Generate embeddings via the embedding model's `LLamaEmbedder` |
| GET | `/v1/models` | List loaded models (OpenAI shape: `object:"list"`, `owned_by:"ecassistant"`, `loaded`, `is_embedding`) |

### ECAssistant Extension Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/eca/health` | Health: status, version, loaded models, session/client counts, uptime |
| POST | `/eca/clients` | Register a client → returns `client_id` (UUID) + server version |
| POST | `/eca/clients/{id}/heartbeat` | Record heartbeat (with `active_sessions`); returns live session count |
| DELETE | `/eca/clients/{id}` | Disconnect client → frees all its sessions |
| POST | `/eca/sessions` | Create a session (own KV cache); checks `MaxSessions` + VRAM budget (503 if over) |
| POST | `/eca/sessions/{id}/prefill` | Prefill a static prefix into the session's KV cache |
| POST | `/eca/sessions/{id}/rewind` | Rewind KV cache to the last saved state |
| POST | `/eca/sessions/{id}/save-state` | Snapshot current KV cache state |
| POST | `/eca/sessions/{id}/reset` | Discard + recreate the KV cache (caller must re-prefill) |
| GET | `/eca/sessions/{id}/status` | Session status: model, prefilled, token count, context, VRAM, timestamps |
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
- **Heartbeat + eviction:** clients send `POST /eca/clients/{id}/heartbeat` on
  `heartbeat_interval_sec` (default 30s). A `Timer` in `ClientManager` runs every
  `heartbeat_timeout_sec` (default 90s) and evicts any client whose last heartbeat is stale,
  freeing all of its sessions.
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
  "logging": { "level": "info", "file": "ecassistant-llm.log" }
}
```

Section semantics:

- **`server`** — binding (`host`/`port` → computed `Prefix`), session cap, VRAM budget, heartbeat cadence. `gpu_layers`, `threads`, `batch_size` are **server-side** concerns (not in Core config).
- **`models`** — one `ModelConfig` each. `gpu_layers` is clamped to `[0,100]`; `threads: -1` → auto; `batch_size: 0` → LLamaSharp default. First non-embedding model = **main**; first embedding model = **embeddings**. Embedding models use `pooling_type` (`mean`/`cls`/`last`/`none`, default `mean`).
- **`inference`** — default sampling params, overridable per-request via `temperature`/`top_p`/`top_k`/`max_tokens`/`repeat_penalty` on the chat request.
- **`logging`** — `level` (`debug`/`info`/`warn`/`error`) + log file path (resolved relative to the config dir).

## Lifecycle

1. **Startup** — `Program.cs` resolves the config path (arg → exe dir → CWD), `LlmServerConfig.TryLoad` + `Validate`, builds `ServerLogger`, constructs `MultiModelHost`/`InferenceScheduler`/`VramBudget` → `LoadAll()`, then `SessionRegistry`, `ClientManager`, `LlmHttpServer`.
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
- **No HTTP framework** — `System.Net.HttpListener` + `System.Text.Json` only; the sole
  external dependency is LLamaSharp.
