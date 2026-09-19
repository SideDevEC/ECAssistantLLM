# ECAssistantLLM — Backend Abstraction Architecture (v1)

**Date:** 2026-09-18
**Goal:** Serve ternary Bonsai-2 models (Qwen3.8-27B ternary, ~5.9 GB) via Prism ML's
llama.cpp fork, cross-platform (macOS Metal / Windows CUDA / Linux CUDA / CPU fallback),
alongside the existing stock LLamaSharp path. Stock path remains the default.

## Design Decision

**ProcessBackend** (subprocess supervision of Prism's `llama-server`), not native interop.
- Prism's ternary kernels exist only in their llama.cpp/MLX forks; stock LLamaSharp refuses the GGUFs.
- MLX is Apple-only and excluded; the llama.cpp fork covers Metal + CUDA + CPU.
- Subprocess isolation survives upstream churn on both sides (LLamaSharp ↔ llama.cpp).

## Class Inventory (one type per file, folder-mirrored namespaces)

### Detection (pure, stateless-eligible logic behind instance classes)

- `Engine/Backends/TernaryModelDetector.cs`
  - `static bool IsTernary(string ggufPath)` → reads GGUF header metadata (GGUF v3),
    returns true when `general.architecture == qwen3.8` AND file declares
    `ternary` / packed formats (`PTQ1_0`, `PQ2_0`) or general.file_type indicates ternary.
  - Stateless utility — no mutable state. Comment: `// Stateless utility — no mutable state`.
- `Engine/Backends/BackendSelector.cs`
  - `ModelBackendKind Select(ModelConfig cfg)` → enum `{ LlamaSharp, Process }`
  - Rule: ternary-detected → Process; explicit `backend` field on ModelConfig overrides;
    otherwise LlamaSharp.
- `Engine/Backends/ModelBackendKind.cs` — enum (file per type rule).

### Runtime provisioning (per-OS, checksummed, INSTALLED BY THE WIZARD — never downloaded by the server)

- `Engine/Backends/RuntimeManifest.cs` — immutable record:
  `RuntimeManifest(PlatformId Platform, string RuntimeId, IReadOnlyList<RuntimeAsset> Assets, string ArchiveRoot, string BinaryRelativePath)`.
- `Engine/Backends/PlatformId.cs` — enum `{ OsxArm64, WinX64, LinuxX64 }`.
- `Engine/Backends/PlatformRuntimeCatalog.cs`
  - `IReadOnlyList<RuntimeManifest> For(PlatformId)` — pinned Prism llama.cpp fork builds
    (osx-arm64/Metal, win-x64/CUDA12, linux-x64/CUDA12, plus CPU fallbacks).
- `Assets/install-manifest.json` (shipped with the server package)
  - The SAME pinned catalog serialized as JSON. The ECAssistant wizard (Core) reads this
    file and installs the runtime + models up front. Core parses the file only — it holds
    ZERO knowledge of LLM internals.
- `Engine/Backends/RuntimeLocator.cs`
  - `string? FindInstalled(string backendsRootDir, RuntimeManifest)`
  - Backends live under `<backendsRoot>/<runtimeId>/`.
- **No fetchers.** ECAssistantLLM NEVER downloads runtimes or weights. Missing pieces are
  hard errors directing the user to the setup wizard.

### Process backend

- `Engine/Backends/ProcessModelInstance.cs`
  - Owns one `llama-server` child process. Constructor-injected:
    `(ModelConfig cfg, string serverBinaryPath, int port, ILogger logger)`.
  - `Task StartAsync(CancellationToken)` — launches with `--model <path> --port <p>
    --ctx-size <n> --n-gpu-layers <g> --threads <t>`, waits until `/health` returns 200.
  - `Task StopAsync()` — graceful SIGTERM → kill after timeout.
  - `string BaseUrl { get; }` — `http://127.0.0.1:<port>`.
  - `IDisposable`; one type per file.
- `Engine/Backends/ProcessModelHost.cs`
  - Cross-process proxy for models served by `llama-server`. Implements
    `IProcessModelHost`: `Task<ProcessModelInstance> StartAsync(ModelConfig, CancellationToken)`,
    `IReadOnlyList<ProcessModelInstance> Instances`.
- `Engine/Backends/IProcessModelHost.cs` — interface (DI seam for tests).

### Request routing

- `Server/RequestRouter` (existing, extended):
  - Model configs whose `Select()` = Process are served by **proxying**:
    `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`, `/v1/models`,
    `/v1/tokenize` are forwarded 1:1 to the child process (OpenAI-compatible),
    streaming via `SseStreamer` passthrough.
  - ECAssistant session extensions (`/eca/*` KV-cache semantics) are **not available**
    for Process models; router returns 501 with a clear message for those combinations.
    Clients fall back to stateless OpenAI behavior (send full history per request).
- `Engine/MultiModelHost` (existing, extended): holds slots for LlamaSharp models only;
  Process models are owned by `ProcessModelHost`. Both are consulted for `/v1/models`.

### Config additions

- `Config/Models/ModelConfig.cs` (existing, extended):
  - `backend`: `"auto"` (default) | `"llamasharp"` | `"process"`
- `Config/Models/BackendsSection.cs` (new):
  - `backends_root` (default `<serverRoot>/backends`), `models_root`.
  - NO download flags — the server never downloads.

## Dependency Flow (new parts)

```
Program.cs
   └─► Server/LlmHttpServer ─► Server/RequestRouter
            │                      ├─► Engine/MultiModelHost   (LlamaSharp models, unchanged)
            │                      └─► Engine/Backends/ProcessModelHost (proxy models)
            │                                └─► ProcessModelInstance ─► llama-server (Prism fork)
            └─► Engine/Backends/BackendSelector ◄─ TernaryModelDetector
                     └─► RuntimeLocator / PlatformRuntimeCatalog
```

`Server → Engine → Config` layering preserved. No new upward or sideways dependencies.
Backends package depends only on Config + ServerLogger.

## Cross-Platform Targets

| Platform | Build | Acceleration |
|---|---|---|
| osx-arm64 | Prism fork, Metal | GPU |
| win-x64 | Prism fork, CUDA 12 | GPU (NVIDIA) |
| linux-x64 | Prism fork, CUDA 12 | GPU (NVIDIA) |
| any CPU fallback | Prism fork, CPU | works everywhere (ternary is light) |

## Testing

- `Engine/Backends/BackendSelectorTests.cs` — ternary detection → Process, override, default.
- `Engine/Backends/TernaryModelDetectorTests.cs` — GGUF header fixtures (valid, non-GGUF, standard).
- `Engine/Backends/RuntimeLocatorTests.cs` — found / missing / corrupted paths.
- `ProcessModelInstance` — integration-tested manually (no network/disk in unit tests).

## Non-Goals (v1)

- No MLX runtime (Apple-only; llama.cpp Metal covers macOS).
- No multi-process scheduling beyond one child per model (InferenceScheduler already
  serializes; llama-server handles its own queueing).

## v2 addendum note — structured decoding on Process models (14.8.2)

Structured mode IS supported on process models (session-scoped). The child gets
`grammar: DecisionGrammar.Gbnf` + `chat_template_kwargs: {enable_thinking: false}`;
the envelope early-stop + StructuredDecoder response are identical to in-process.
Stateless process requests stay raw 1:1 passthrough (structured without session → 400).
Verified E2E on the Prism runtime with real Bonsai weights (3/3 valid envelopes).

---

# Backend Abstraction Architecture — v2 Addendum: Process Sessions (2026-09-19)

**Goal:** Process-backend models behave EXACTLY like in-process models from the client's
perspective — including `session_id`, prefill, rewind, save-state, reset, status, destroy.
No more 400 on `session_id`. Port allocation moves to a higher random range.

## Design Decision

**Transcript-backed sessions + child prefix-cache**, not child-side slot save/restore.
- `ProcessSession` keeps the conversation transcript server-side (prefix + history).
- Each turn re-sends the full history to the child; llama-server's per-slot KV/prefix
  cache skips tokens it already processed → long-chat efficiency without fork-specific
  `/slots` APIs (works with ANY OpenAI-compatible child).
- Efficiency is inherited from the child's cache; correctness never depends on it.

## New Classes (one type per file, folder-mirrored namespaces)

- `Engine/Backends/BackendPortAllocator.cs`
  - `int Allocate(IReadOnlyCollection<int> usedPorts)` — random port in a configurable
    range (default 20000–25000), skipping used ports. Constructor-injected `Random`.
- `Engine/Backends/ProcessSession.cs`
  - One client session on a Process-backend model. Holds transcript, serializes
    outbound OpenAI chat payloads (incl. image_url parts), streams deltas from the child.
  - Methods mirror `SessionContext`: `PrefillAsync`, `InferAsync`, `SaveState`,
    `RewindAsync`, `Reset`, plus status properties (`IsPrefilled`, `ApproxTokenCount`,
    `ContextSize`, `EstimatedVramMb = 0` — KV lives in the child process).
  - Transcript mutations (SaveState/Rewind/Reset/Infer) serialized by an internal
    `SemaphoreSlim` IO lock, same contract as `SessionContext`.
- `Engine/Backends/ProcessSessionRegistry.cs`
  - Mirrors `SessionRegistry` API for process sessions: `Create`, `Get`, `Destroy`,
    `DestroyClient`, `CountForClient`. Enforces `Server.MaxSessions`. Thread-safe.

## Config additions

- `Config/Models/BackendsSection.cs`: `port_min` / `port_max` (defaults 20000/25000).

## Wiring changes

- `RequestRouter`: `IsProcessModel` branch now handles `session_id` via
  `ProcessSessionRegistry`; stateless requests still proxy 1:1. Structured mode remains
  LlamaSharp-only (server-side grammar) → clean 400 for process models.
- `LlmHttpServer`/`Program.cs`: construct and inject `ProcessSessionRegistry`.
- `ClientManager`: optional `ProcessSessionRegistry` — destroys process sessions on
  client disconnect/eviction, symmetric with LLamaSharp sessions.

## Dependency Flow (v2 additions)

```
RequestRouter
   ├─► ProcessSessionRegistry ─► ProcessSession ─► HttpClient ─► child llama-server
   └─► ProcessModelHost (stateless proxy path, unchanged)
```

`Server → Engine → Config` layering preserved. Process sessions deliberately do NOT
reserve VramBudget (weights + KV are owned by the child process, not this server).

## Testing

- `Tests/Backends/BackendPortAllocatorTests.cs` — range bounds, used-port skips, exhaustion.
- `Tests/Backends/ProcessSessionRegistryTests.cs` — create/duplicate/max/destroy, transcript
  save/rewind/reset semantics (no HTTP — transcript logic is isolated from the transport).
