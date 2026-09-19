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

## v2 addendum note — structured decoding on Process models (14.8.2 → 14.8.5)

Structured mode IS supported on process models — session-scoped (14.8.2) AND stateless
(14.8.4). The child gets `grammar: DecisionGrammar.Gbnf` + `chat_template_kwargs:
{enable_thinking: false}`; the envelope early-stop + StructuredDecoder response are
identical to in-process. Verified E2E on the Prism runtime with real Bonsai weights.

**100% parity (14.8.4/14.8.5):** stateless process chat also runs the in-process code
shapes — ThinkFilter on streamed + non-streamed output, identical response objects,
structured without session. The raw 1:1 proxy is UNREACHABLE from /v1/chat/completions
(all four combinations handled explicitly); it was removed after E2E proved it silently
swallowed stateless structured requests (14.8.5 regression fix, 10/10 E2E green).
Sampling on ALL process payloads is driven by `ProcessPayloadFactory` with the same
in-process defaults (temp 0.3 / top_p 0.95 / top_k 40 / repeat_penalty 1.1) — child
llama-server defaults (temp 1.0) can never leak through.

---

# Backend Abstraction Architecture — v3 Addendum: Reliability Hardening (2026-09-19)

**Goal:** kill the parent server however you like (SIGKILL, crash, power loss) and the
system self-heals: no orphaned children, no port collisions, no corrupted state.
Zero extra processes.

## PID files + safe orphan reap (14.8.7)

- `ProcessModelInstance` writes `backends/<model>.pid` (`pid<TAB>binaryName`) once the
  child is healthy; deletes it on graceful stop.
- `ReapOrphan(pidFile, logger)` runs BEFORE spawning a fresh child:
  - child dead → stale record deleted;
  - child alive AND process name matches the recorded binary name (equality OR prefix —
    macOS truncates `Process.ProcessName` to 15 chars, p_comm limit) → killed with its
    whole tree (the llama-server-left-behind scenario);
  - **PID-reuse guard:** name mismatch → process NEVER killed, record discarded;
  - malformed/unreadable files → cleaned up.

## OS-verified port allocation (14.8.7)

`ProcessModelHost.AllocatePort` probe-binds every allocator candidate on loopback
(`BackendPortAllocator.IsPortFree`) before committing — ports held by orphans or any
unrelated service are skipped instead of colliding. Max 10 probes, then a clear error.

## SSE byte-determinism (14.8.7)

`SseStreamer` writes explicit `\n\n` terminators — `StreamWriter.WriteLineAsync` would
translate to `\r\n` on Windows (`Environment.NewLine`), making SSE bodies OS-dependent.
Now byte-identical on macOS/Linux/Windows.

## CI: 3-OS test matrix (tests.yml)

Unit suite (`Category != E2E` — E2E needs real GGUF weights, stays local) runs on
**ubuntu / macos / windows** runners on every push to main + manual dispatch. The very
first matrix run caught two real cross-platform bugs (SSE \r\n on Windows; macOS
ProcessName truncation defeating the reap guard). Windows-only process-spawn tests are
platform-guarded; everything else runs everywhere.

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
  `ProcessSessionRegistry`; stateless process chat runs through `ProcessStatelessClient`
  with in-process code shapes (ThinkFilter, structured, response objects). The raw 1:1
  proxy is no longer reachable from chat (14.8.5). Structured mode works for both
  session and stateless process requests (grammar via child).
- `LlmHttpServer`/`Program.cs`: construct and inject `ProcessSessionRegistry`.
- `ClientManager`: optional `ProcessSessionRegistry` — destroys process sessions on
  client disconnect/eviction, symmetric with LLamaSharp sessions.

## Dependency Flow (v2 additions)

```
RequestRouter
   ├─► ProcessSessionRegistry ─► ProcessSession ─► ProcessPayloadFactory ─► HttpClient ─► child llama-server
   └─► ProcessStatelessClient ────┘ (stateless; same factory, no transcript)
```

`Server → Engine → Config` layering preserved. Process sessions deliberately do NOT
reserve VramBudget (weights + KV are owned by the child process, not this server).

## Testing

- `Tests/Backends/BackendPortAllocatorTests.cs` — range bounds, used-port skips, exhaustion.
- `Tests/Backends/ProcessSessionRegistryTests.cs` — create/duplicate/max/destroy, transcript
  save/rewind/reset semantics (no HTTP — transcript logic is isolated from the transport).

## v3 addendum note — Vulkan DeltaNet-MoE guard (14.9.0)

**Vulkan + `qwen3_5moe` family + `gpu_layers > 0` crashes** (partial offload,
llama.cpp issue #26945, unfixed upstream as of 2026-09-19). At model load,
`ModelSlot` computes the effective GPU layers via:

- `GgufArchitectureReader` — reads `general.architecture` from the GGUF header
- `VulkanAvailabilityProbe` — Vulkan is primary iff Windows/Linux AND no CUDA driver
- `GpuLayerGuard.Compute` — clamps to 0 + logs reason when the crash combo is detected

Config `gpu_layers` is never rewritten; Metal/CUDA machines and dense models are
unaffected. Remove this guard once upstream ships the fix.

## v3 addendum note — shutdown grace period (14.9.3)

**Problem:** `shutdown_on_last_client` fired instantly on the last disconnect — a
client reconnect cycle (register → disconnect → register) killed the server, and
the app's next chat request hit a dead server (404). Seen on fresh installs.

**Fix:** `ClientManager.StartGraceCountdown` — the shutdown callback now waits
`server.shutdown_grace_sec` (default **60**; 0 = legacy immediate). Any client
registration during the window cancels the countdown and resets the fired-guard;
expiry re-checks that clients are still gone before firing. Grace 0 keeps legacy
behavior (ServerIdleShutdownTests unaffected).

**Also (14.9.3):** `LlmHttpServer` suppresses `ObjectDisposedException` in the
unhandled-error handler — a disposed-response race during shutdown was logging
spurious "Cannot access a disposed object" errors.

## v3 addendum note — audit fixes (14.9.3+, unreleased until next tag)

- **Grace-timer race:** timer now created BEFORE publishing via `Interlocked.Exchange`;
  cancel disposes the exchanged value — reconnect races can no longer orphan or
  double-fire the shutdown countdown.
- **Per-model `max_tokens` now covers process-backend models and cold starts**
  (config lookup instead of `_slots`-only), and the global `inference.max_tokens`
  default is honored on stateless chat paths (was session-only).
- `GenerateDefault` writes `shutdown_grace_sec` so the knob is visible in fresh configs.
- `ProcessModelInstance`: comment documenting the backend gpu-layers clamp gap (Vulkan+process is currently unreachable).
- `LlmHttpServer`: disposed-response races logged-quiet (documented tradeoff: a genuine router bug disposing a live response would also be silenced).
