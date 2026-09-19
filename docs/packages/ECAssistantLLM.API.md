# ECAssistantLLM.API.md

Types: 124  |  LOC: 9004  |  ~5364 tokens

---

### Interface: IClientManager
> Interface for managing client connections: registration, heartbeat, eviction.
Properties:
  - int ClientCount { get; set; }
Methods:
  - string Register(string clientName, string? version = null)
  - bool Heartbeat(string clientId, int activeSessions)
  - bool Disconnect(string clientId)
  - bool IsValid(string clientId)
  - void Dispose()
Cross-package deps: ECAssistant.LLM.Engine

### Interface: IInferenceScheduler
> Interface for serializing inference across all clients.
Properties:
  - int QueueDepth { get; set; }
Methods:
  - Task<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)

### Interface: ILogger
> Simple file + console logger for the LLM server.
Methods:
  - void Info(string tag, string message)
  - void Warn(string tag, string message)
  - void Error(string tag, string message)
  - void Debug(string tag, string message)

### Interface: IProcessModelHost
> Abstraction over the externally-served (subprocess) model host. DI seam for tests.
Properties:
  - IReadOnlyList<ProcessModelInstance> Instances { get; set; }
Methods:
  - Task<ProcessModelInstance> EnsureStartedAsync(ModelConfig config, CancellationToken ct = default)
  - Task<string> EnsureStartedUrlAsync(string modelId, CancellationToken ct = default)
  - Task StopAllAsync()
Cross-package deps: ECAssistant.LLM.Config

### Interface: IRequestRouter
> Interface for routing incoming HTTP requests to the appropriate handler.
Methods:
  - Task RouteAsync(HttpListenerContext ctx, CancellationToken ct)

### Class: BackendPortAllocator
> Allocates TCP ports for child llama-server processes from a configurable,
Constructor:
  - BackendPortAllocator(BackendsSection backends, Random? random = null)
Cross-package deps: ECAssistant.LLM.Config

### Class: BackendPortAllocatorTests
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: BackendSelector
> Chooses the execution backend for a model configuration. Ternary-packed models are
Cross-package deps: ECAssistant.LLM.Config

### Class: BackendSelectorTests
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, Xunit

### Class: BackendsSection
> Configuration for the backend subsystem: where the pre-installed external

### Class: ChatCompletionChunk
> SSE streaming chunk (OpenAI format).

### Class: ChatCompletionRequest
> OpenAI-compatible chat completion request.

### Class: ChatCompletionTests
> Tests for OpenAI-compatible chat completions (streaming + non-streaming),
Constructor:
  - ChatCompletionTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ChatMessage
> OpenAI-compatible chat completion request.

### Class: ChatMessageContentConverter
> OpenAI-compatible chat completion request.
Implements: JsonConverter<List<ChatMessage>>

### Class: ChatMessageContentConverterTests
> Multimodal content parsing: plain strings, content-part arrays, data-URI images.
Cross-package deps: ECAssistant.LLM.Models, Xunit

### Class: ChunkChoice
> SSE streaming chunk (OpenAI format).

### Class: ChunkDelta
> SSE streaming chunk (OpenAI format).

### Class: ClientAuthTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ClientManagementTests
> Tests for client registration, heartbeat and disconnect
Constructor:
  - ClientManagementTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ClientManager
> Manages client connections: registration, heartbeat, eviction.
Implements: IClientManager, IDisposable
Constructor:
  - ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, Action? onLastClientDisconnected, SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, Action? onLastClientDisconnected, Engine.Backends.ProcessSessionRegistry? processSessionRegistry)
Cross-package deps: ECAssistant.LLM.Interfaces

### Class: ClientManagerGraceTests
> ClientManager shutdown grace: last-client disconnect starts a countdown; a client
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Interfaces, Xunit

### Class: ClientRegisterRequest
> Generic API error response.

### Class: ClientRegisterResponse
> Generic API error response.

### Class: CompletionChoice
> OpenAI-compatible text completion request.

### Class: CompletionChunk
> OpenAI-compatible text completion request.

### Class: CompletionChunkChoice
> OpenAI-compatible text completion request.

### Class: CompletionRequest
> OpenAI-compatible text completion request.

### Class: CompletionResponse
> OpenAI-compatible text completion request.

### Class: ConcurrentRequestTests
> Tests for concurrent access to the LLM server.
Constructor:
  - ConcurrentRequestTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: CreateSessionRequest
> Generic API error response.

### Class: DecisionEnvelope
> v13 structured decision envelope — the grammar-forced output shape.

### Class: DecisionGrammar
> v13 GBNF grammar that forces the model's output into the decision envelope

### Class: DecisionToolCall
> v13 structured decision envelope — the grammar-forced output shape.

### Class: EmbeddingData

### Class: EmbeddingRequest

### Class: EmbeddingResponse

### Class: EmbeddingsTests
> Tests for the OpenAI-compatible embeddings endpoint.
Constructor:
  - EmbeddingsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ErrorDetail
> Generic API error response.

### Class: ErrorHandlingTests
> Cross-cutting error-handling tests: unknown paths, wrong methods,
Constructor:
  - ErrorHandlingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ErrorResponse
> Generic API error response.

### Class: GgufArchitectureReader
> Reads the <c>general.architecture</c> metadata value from a GGUF header.

### Class: GgufArchitectureReaderTests
> GgufArchitectureReader against minimal synthetic GGUF streams.
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: GpuLayerGuard
> Clamps configured GPU layers when the combination would hit a known upstream crash.

### Class: GpuLayerGuardTests
> GpuLayerGuard decision table: only Vulkan + DeltaNet-MoE + configured layers clamps.
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: HealthTests
> Tests for GET /eca/health.
Constructor:
  - HealthTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: HeartbeatRequest
> Generic API error response.

### Class: HeartbeatResponse
> Generic API error response.

### Class: HeartbeatTests
> Tests for heartbeat mechanism. Uses the main TestServerFixture (timeout 300s).
Constructor:
  - HeartbeatTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: InferenceDefaults
> Default inference parameters. Can be overridden per request.

### Class: InferenceScheduler
> Serializes inference across all clients.
Implements: IInferenceScheduler
Constructor:
  - InferenceScheduler(ILogger logger)
Cross-package deps: ECAssistant.LLM.Interfaces

### Class: InvalidDecisionException
> v13 Parses the grammar-forced decision envelope into a typed DTO.
Implements: Exception
Constructor:
  - InvalidDecisionException(string message)
Cross-package deps: ECAssistant.LLM.Models

### Class: KvCacheTests
> Tests for KV-cache operations: prefill, save-state, rewind, reset, and the
Constructor:
  - KvCacheTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: LlmHttpServer
> Main HTTP server using HttpListener. Routes requests to OpenAI and ECAssistant endpoints.
Implements: IDisposable
Constructor:
  - LlmHttpServer(LlmServerConfig config, MultiModelHost modelHost, SessionRegistry sessionRegistry, IInferenceScheduler scheduler, VramBudget vramBudget, IClientManager clientManager, ILogger logger, CancellationTokenSource? externalCts = null, IProcessModelHost? processModelHost = null, Engine.Backends.ProcessSessionRegistry? processSessionRegistry = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Interfaces, ECAssistant.LLM.Models, ECAssistant.LLM.Server

### Class: LlmServerConfig
> Root server configuration. Deserialized from llm-server.json.

### Class: LlmServerConfigTests
> Config validation and JSON Save/Load round-trip tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config

### Class: LlmServerInfo
> Single source of truth for the server version string.

### Class: LoadModelRequest
> Generic API error response.

### Class: LoggingSection
> Logging configuration for the server.

### Class: MalformedRequestTests
> Tests for malformed requests — valid JSON but wrong types, missing fields,
Constructor:
  - MalformedRequestTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ModelConfig
> Configuration for a single model hosted by the server.

### Class: ModelLoadTests
> Tests for runtime model management: /eca/models/load, /eca/models/unload.
Constructor:
  - ModelLoadTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ModelPathPolicyTests
> Pure-logic tests for model-path containment (models_root traversal guard).
Cross-package deps: ECAssistant.LLM.Server

### Class: ModelPathRestrictionTests
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ModelSlot
> One loaded model: weights + params + status.
Implements: IDisposable
Constructor:
  - ModelSlot(string id, ModelConfig config, ILogger logger, string rootDir)
Cross-package deps: LLama, LLama.Common, LLama.Native, ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: ModelSmokeE2E
> Cross-OS model smoke test — runs our REAL server (in-process LLamaSharp path)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: ModelsTests
> Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
Constructor:
  - ModelsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: MtmdMarkerResolver
> Retrieves the projector-specific media marker token used by LLamaSharp's MTMD tokenizer.
Cross-package deps: LLama

### Class: MultiModelHost
> Manages multiple loaded models (at least 2: main + embeddings).
Implements: IDisposable
Constructor:
  - MultiModelHost(LlmServerConfig config, ILogger logger, string rootDir, BackendSelector? backendSelector = null)
Cross-package deps: LLama, ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: PlatformDetector
> Detects the current runtime platform. Stateless utility — no mutable state.

### Class: PlatformRuntimeCatalog
> Pinned catalog of external backend runtime builds per platform.

### Class: PrefillRequest
> Generic API error response.

### Class: PrefillResponse
> Generic API error response.

### Class: ProcessModelHost
> Owns and supervises llama-server subprocess instances for Process-backend models.
Implements: IProcessModelHost, IDisposable
Constructor:
  - ProcessModelHost(LlmServerConfig config, ILogger logger, string serverRoot, PlatformRuntimeCatalog? catalog = null)
Cross-package deps: ECAssistant.LLM.Config

### Class: ProcessModelInstance
> One externally-served model: owns a llama-server child process and its port.
Implements: IDisposable
Constructor:
  - ProcessModelInstance(ModelConfig config, string serverBinaryPath, int port, ILogger logger, string? pidFilePath = null)
Cross-package deps: ECAssistant.LLM.Config

### Class: ProcessOrphanReapTests
> Cross-platform orphan-proofing: PID files + safe reaping (binary-name guard so a
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Server, Xunit

### Class: ProcessSession
> One client session on a Process-backend model (child llama-server).
Implements: IDisposable
Constructor:
  - ProcessSession(string clientId, string sessionId, ModelConfig model, IProcessModelHost host, HttpClient httpClient, ILogger logger)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Models

### Class: ProcessSessionRegistry
> Registry of client sessions on Process-backend models (child llama-server).
Implements: IDisposable
Constructor:
  - ProcessSessionRegistry(IProcessModelHost host, LlmServerConfig config, ILogger logger, Func<HttpClient>? httpClientFactory = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Models

### Class: ProcessSessionRegistryTests
> Stub host — never starts a real child; url resolution fails fast.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, Xunit

### Class: ProcessSessionStructuredTests
> Structured mode on process-backend models: grammar + enable_thinking=false reach
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: ProcessStatelessClientTests
> Stateless process inference: sampling parity (explicit in-process defaults, never
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: ProxyRequestHandler
> Stateless 1:1 proxy: forwards an incoming HttpListener request to a target base URL

### Class: RequestRouter
> Routes incoming HTTP requests to the appropriate handler.
Implements: IRequestRouter
Constructor:
  - RequestRouter(MultiModelHost models, SessionRegistry sessions, IInferenceScheduler scheduler, VramBudget vram, IClientManager clients, LlmServerConfig config, ILogger logger, CancellationTokenSource cts, IProcessModelHost? processHost = null, ProcessSessionRegistry? processSessions = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Interfaces, ECAssistant.LLM.Models

### Class: RewindResponse
> Generic API error response.

### Class: RootPathGuard
> Root-confinement guard: the server must never write outside its root directory

### Class: RootPathGuardTests
> RootPathGuard: paths inside the root pass through; absolute paths outside the
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: RouteMethodTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: RoutingTests
> Tests for client routing and session namespacing:
Constructor:
  - RoutingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: RuntimeLocator
> Locates previously-installed backend runtimes under the backends root directory.

### Class: RuntimeLocatorTests
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: SecurityHarness
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ServerCollection
> Shared collection so all integration tests use the single
Implements: TestServerFixture>
Cross-package deps: Xunit

### Class: ServerIdleShutdownTests
> Regression for the 2026-09-18/19 zombie: HttpListener.GetContextAsync is not
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server, Xunit

### Class: ServerLogTests
> Tests that the server creates log files and writes meaningful entries
Constructor:
  - ServerLogTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ServerLogger
> Simple file + console logger for the LLM server.
Implements: ILogger
Constructor:
  - ServerLogger(LogLevel minLevel = LogLevel.Info, string? logFile = null)

### Class: ServerSection
> Server binding and lifecycle settings.

### Class: SessionContext
> Per-session inference state: own InteractiveExecutor + KV cache.
Implements: IDisposable
Constructor:
  - SessionContext(string clientId, string sessionId, string modelId, LLamaWeights weights, ModelParams modelParams, InferenceParams inferenceParams, ILogger logger, MtmdWeights? mtmd = null)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config

### Class: SessionLifecycleTests
> Tests for session lifecycle: create, status, destroy, and validation.
Constructor:
  - SessionLifecycleTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SessionRegistry
> Registry of all client sessions across the server.
Implements: IDisposable
Constructor:
  - SessionRegistry(MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger, MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger, VramBudget? vram)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config, ECAssistant.LLM.Interfaces

### Class: ShutdownTests
> Tests for the /eca/shutdown endpoint.
Constructor:
  - ShutdownTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SseStreamer
> Writes SSE (Server-Sent Events) streaming responses for OpenAI-compatible endpoints.
Cross-package deps: ECAssistant.LLM.Models

### Class: SseStreamerTests
> SSE framing tests over a real loopback HttpListener — no model weights involved.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Server

### Class: StructuredDecoder
> v13 Parses the grammar-forced decision envelope into a typed DTO.
Cross-package deps: ECAssistant.LLM.Models

### Class: StructuredDecoderTests
> v13 structured decision envelope decoding.
Cross-package deps: ECAssistant.LLM.Engine, Xunit

### Class: SuccessResponse
> Generic API error response.

### Class: TernaryModelDetector
> Detects ternary-packed GGUF models (e.g. Prism ML Bonsai-2) by reading the GGUF

### Class: TernaryModelDetectorTests
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: TestServerFixture
> Shared integration-test fixture. Starts the real ECAssistantLLM HTTP server
Implements: IAsyncLifetime
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ThinkFilter
> Stream-safe filter that removes reasoning-model thinking blocks

### Class: ThinkFilterTests
> v12.8 regression: reasoning models (Qwen3.5) leak &lt;think&gt;…&lt;/think&gt; blocks and
Cross-package deps: ECAssistant.LLM.Server, Xunit

### Class: TokenizeRequest

### Class: TokenizeResponse

### Class: TokenizeTests
> Tests for the ECA tokenize endpoint (POST /eca/tokenize).
Constructor:
  - TokenizeTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: VisionImageSuiteE2ETests
> FULL-SYSTEM vision image suite (Category=E2E): runs against an EXTERNALLY
Implements: IAsyncLifetime
Cross-package deps: Xunit

### Class: VisionInferenceE2ETests
> FULL-SYSTEM vision E2E (Category=E2E, excluded from unit runs):
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server, Xunit

### Class: VramBudget
> Tracks total VRAM usage across all sessions.
Constructor:
  - VramBudget(LlmServerConfig config)
Cross-package deps: ECAssistant.LLM.Config

### Class: VramBudgetTests
> Pure-logic tests for VRAM budget reserve/release/exceed accounting.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine

### Class: VulkanAvailabilityProbe
> Determines whether Vulkan is the GPU backend an in-process LLamaSharp model would use.

### Record: ClientInfo
> Manages client connections: registration, heartbeat, eviction.
Constructor:
  - ClientInfo(string Id, string Name, string Version, DateTime RegisteredAt, DateTime LastHeartbeat, int ActiveSessions)
Cross-package deps: ECAssistant.LLM.Interfaces

### Record: GpuLayerDecision
> Result of a GPU-layer guard decision: the layer count that should actually be used,
Constructor:
  - GpuLayerDecision(int EffectiveGpuLayers, bool Clamped, string? Reason)

### Record: ModelInfo
> Manages multiple loaded models (at least 2: main + embeddings).
Constructor:
  - ModelInfo(string Id, string Path, bool IsLoaded, bool IsEmbedding, int GpuLayers, uint ContextSize, int EmbeddingDim)
Cross-package deps: LLama, ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Record: RuntimeAsset
> One downloadable archive belonging to a backend runtime.
Constructor:
  - RuntimeAsset(string Url, string Sha256)

### Record: SessionStatusInfo
> Registry of all client sessions across the server.
Constructor:
  - SessionStatusInfo(string ClientId, string SessionId, string ModelId, bool IsPrefilled, int ApproxTokenCount, uint ContextSize, double EstimatedVramMb, DateTime CreatedAt, DateTime LastActivity)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config, ECAssistant.LLM.Interfaces

### Record: VisionImage
> OpenAI-compatible chat completion request.
Constructor:
  - VisionImage(string MimeType, byte[] Data)
