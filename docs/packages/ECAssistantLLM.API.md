# ECAssistantLLM.API.md

Types: 153  |  LOC: 12972  |  ~6678 tokens

---

### Interface: IClientManager
> Interface for managing client connections: registration and disconnection.
Properties:
  - int ClientCount { get; set; }
Methods:
  - string Register(string clientName, string? version = null)
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
Cross-package deps: ECAssistant.LLM.Config

### Class: BackendSelectorTests
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, Xunit

### Class: BackendsSection
> Configuration for the backend subsystem: where the pre-installed external

### Class: BatchBackwardCompatTests
> Integration tests for backward compatibility — verifies that the standard
Constructor:
  - BatchBackwardCompatTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: BatchChatCompletionTests
> Integration tests for chat completions via the batch path.
Constructor:
  - BatchChatCompletionTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: BatchConfigTests
> Unit tests for BatchSessionRegistry — session CRUD, namespacing, limits.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine

### Class: BatchInferenceCoordinator
> Coordinates batched inference across all active batch sessions.
Implements: IDisposable
Constructor:
  - BatchInferenceCoordinator(IInferenceContext context, IConversationPool pool, IInferenceModel? model, IVisionEncoder? vision, ILogger logger, string modelId)
Cross-package deps: ECAssistantInference.Abstractions, ECAssistantInference.Models

### Class: BatchServerCollection
> Collection definition for the batch test server fixture.
Implements: ICollectionFixture<BatchServerFixture>
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: BatchServerFixture
> Second test fixture — identical to <see cref="TestServerFixture"/> but with
Implements: IAsyncLifetime
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Server

### Class: BatchSession
Implements: IDisposable
Constructor:
  - BatchSession(string clientId, string sessionId, string modelId, BatchInferenceCoordinator coordinator, ILogger logger)
Cross-package deps: ECAssistantInference.Abstractions, ECAssistantInference.Models

### Class: BatchSessionBufferTests
> Unit tests for the concurrency-hardened buffer system — no model required.
Cross-package deps: ECAssistant.LLM.Engine

### Class: BatchSessionLifecycleTests
> Integration tests for the session lifecycle.
Constructor:
  - BatchSessionLifecycleTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: BatchSessionRegistry
> Registry of batch sessions (sub-agent / stateless) when <c>continuous_batching</c>
Implements: IDisposable
Constructor:
  - BatchSessionRegistry(BatchedExecutorHost host, LlmServerConfig config, ILogger logger)
Cross-package deps: ECAssistant.LLM.Config

### Class: BatchSessionRegistryTests
> Unit tests for BatchSessionRegistry — session CRUD, namespacing, limits.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine

### Class: BatchVsStandardComparisonTests
> Collection definition for the batch test server fixture.
Constructor:
  - BatchVsStandardComparisonTests(BatchServerFixture batchFixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: BatchedExecutorHost
> Owns BatchInferenceCoordinator instances — one per loaded in-process model —
Implements: IDisposable
Constructor:
  - BatchedExecutorHost(MultiModelHost modelHost, LlmServerConfig config, ILogger logger)
Cross-package deps: ECAssistantInference.Abstractions, ECAssistantInference.Models, ECAssistant.LLM.Config

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
> Tests for client registration and disconnect
Constructor:
  - ClientManagementTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ClientManager
> Manages client connections: registration, heartbeat, eviction.
Implements: IClientManager, IDisposable
Constructor:
  - ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, Action? onLastClientDisconnected = null, Engine.Backends.ProcessSessionRegistry? processSessionRegistry = null, BatchSessionRegistry? batchSessionRegistry = null)
Cross-package deps: ECAssistant.LLM.Interfaces

### Class: ClientManagerAlwaysAliveTests
> Always-alive contract: the server NEVER self-shuts down. Last-client disconnect
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

### Class: DecisionGrammarBuildTests
> v14.12.1: DecisionGrammar.BuildGbnf — tool-name union grammar building.
Cross-package deps: ECAssistant.LLM.Engine, Xunit

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

### Class: EnvelopeSalvager
> v15: Repair pass for failed decision-envelope decodes. Two content-preserving
Cross-package deps: ECAssistant.LLM.Models

### Class: EnvelopeSalvagerTests
> v15 (Emre, 2026-09-24): pure-logic tests for truncated-envelope salvage.
Cross-package deps: ECAssistant.LLM.Engine, ECAssistant.LLM.Models

### Class: ErrorDetail
> Generic API error response.

### Class: ErrorHandlingTests
> Cross-cutting error-handling tests: unknown paths, wrong methods,
Constructor:
  - ErrorHandlingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ErrorResponse
> Generic API error response.

### Class: EvaluateRequest
> Generic API error response.

### Class: EvaluateResponse
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

### Class: GrammarProbeTests
Cross-package deps: ECAssistant.LLM.Engine, ECAssistant.LLM.Models, Xunit

### Class: HealthTests
> Tests for GET /eca/health.
Constructor:
  - HealthTests(TestServerFixture fixture)
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

### Class: InvalidToolCallException
> One entry of the OpenAI-native `tools` request field: a function the client
Implements: Exception
Constructor:
  - InvalidToolCallException(string message)

### Class: JsonSchemaGrammarConverter
> Converts a (subset of) JSON Schema into GBNF grammar fragments the llama.cpp

### Class: KvCacheTests
> Tests for KV-cache operations: prefill, save-state, rewind, reset, and the
Constructor:
  - KvCacheTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: LlmHttpServer
> Main HTTP server using HttpListener. Routes requests to OpenAI and ECAssistant endpoints.
Implements: IDisposable
Constructor:
  - LlmHttpServer(LlmServerConfig config, MultiModelHost modelHost, SessionRegistry sessionRegistry, IInferenceScheduler scheduler, VramBudget vramBudget, IClientManager clientManager, ILogger logger, CancellationTokenSource? externalCts = null, IProcessModelHost? processModelHost = null, Engine.Backends.ProcessSessionRegistry? processSessionRegistry = null, BatchSessionRegistry? batchSessionRegistry = null)
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
> One loaded model: inference model + config + status.
Implements: IDisposable
Constructor:
  - ModelSlot(string id, ECAssistant.LLM.Config.ModelConfig config, ILogger logger, string rootDir)
Cross-package deps: ECAssistantInference.Abstractions, ECAssistantInference.Exceptions, ECAssistantInference.Implementation, ECAssistantInference.Models, ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: ModelSmokeE2E
> Cross-OS model smoke test — runs our REAL server (in-process LLamaSharp path)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: ModelsTests
> Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
Constructor:
  - ModelsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: MtmdMarkerResolver
> Resolves the MTMD image marker for vision prompts.
Cross-package deps: ECAssistantInference.Abstractions

### Class: MultiModelHost
Implements: IDisposable
Constructor:
  - MultiModelHost(LlmServerConfig config, ILogger logger, string rootDir, BackendSelector? backendSelector = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: NativeToolsTests
> Tests for native OpenAI tools support: schema → GBNF conversion, tool-call
Cross-package deps: ECAssistant.LLM.Engine, ECAssistant.LLM.Models, Xunit

### Class: OpenAiFunctionSpec
> One entry of the OpenAI-native `tools` request field: a function the client

### Class: OpenAiToolSpec
> One entry of the OpenAI-native `tools` request field: a function the client

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
  - RequestRouter(MultiModelHost models, SessionRegistry sessions, IInferenceScheduler scheduler, VramBudget vram, IClientManager clients, LlmServerConfig config, ILogger logger, CancellationTokenSource cts, IProcessModelHost? processHost = null, ProcessSessionRegistry? processSessions = null, BatchSessionRegistry? batchSessions = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistantInference.Abstractions, ECAssistantInference.Models, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Interfaces, ECAssistant.LLM.Models

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
  - ServerLogger(LogLevel minLevel = LogLevel.Info, string? logFile = null, Func<string, bool>? componentFilter = null)

### Class: ServerSection
> Server binding and lifecycle settings.

### Class: SessionContext
> Per-session inference state: own executor + KV cache.
Implements: IDisposable
Constructor:
  - SessionContext(string clientId, string sessionId, string modelId, IInferenceModel model, ECAssistantInference.Models.ModelConfig modelConfig, ECAssistantInference.Models.ContextConfig ctxConfig, SamplingConfig defaultSampling, ILogger logger, IVisionEncoder? vision = null)
Cross-package deps: ECAssistantInference.Abstractions, ECAssistantInference.Exceptions, ECAssistantInference.Models, ECAssistant.LLM.Config

### Class: SessionEvaluateEndpointTests
> v15 (Emre, 2026-09-24): endpoint tests for the KV-hygiene surface —
Constructor:
  - SessionEvaluateEndpointTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SessionLifecycleTests
> Tests for session lifecycle: create, status, destroy, and validation.
Constructor:
  - SessionLifecycleTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SessionRegistry
Implements: IDisposable
Constructor:
  - SessionRegistry(MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger, MultiModelHost modelHost, IInferenceScheduler scheduler, LlmServerConfig config, ILogger logger, VramBudget? vram)
Cross-package deps: ECAssistantInference.Models, ECAssistant.LLM.Config, ECAssistant.LLM.Interfaces

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

### Class: StopFilter
> Stream-safe stop-sequence filter. Terminates generation as soon as any stop

### Class: StopFilterTests
> Migration regression (LLamaSharp → ECAssistantInference): the native engine has
Cross-package deps: ECAssistant.LLM.Server, Xunit

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

### Class: ToolCallDecoder
> Decodes a grammar-forced tool_calls generation (OpenAI wire shape array) into
Cross-package deps: ECAssistant.LLM.Models

### Class: ToolCallGrammarFactory
> Builds a GBNF grammar that forces the model's output into the OpenAI

### Class: ToolsetFingerprint
> Deterministic fingerprint of a toolset (OpenAI tools array) used for
Cross-package deps: ECAssistant.LLM.Models

### Class: ToolsetFingerprintTests
> v14.9: toolset fingerprint — deterministic, order-insensitive (sorted by
Cross-package deps: ECAssistant.LLM.Engine, ECAssistant.LLM.Models, Xunit

### Class: UniqueRuleNames
> Converts a (subset of) JSON Schema into GBNF grammar fragments the llama.cpp

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
> Determines whether Vulkan would be the GPU backend for in-process inference.

### Record: GpuLayerDecision
> Result of a GPU-layer guard decision: the layer count that should actually be used,
Constructor:
  - GpuLayerDecision(int EffectiveGpuLayers, bool Clamped, string? Reason)

### Record: ModelInfo
Constructor:
  - ModelInfo(string Id, string Path, bool IsLoaded, bool IsEmbedding, int GpuLayers, uint ContextSize, int EmbeddingDim)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Record: RuntimeAsset
> One downloadable archive belonging to a backend runtime.
Constructor:
  - RuntimeAsset(string Url, string Sha256)

### Record: SessionStatusInfo
Constructor:
  - SessionStatusInfo(string ClientId, string SessionId, string ModelId, bool IsPrefilled, int ApproxTokenCount, uint ContextSize, double EstimatedVramMb, DateTime CreatedAt, DateTime LastActivity)
Cross-package deps: ECAssistantInference.Models, ECAssistant.LLM.Config, ECAssistant.LLM.Interfaces

### Record: ToolCall
> One entry of the OpenAI-native `tools` request field: a function the client
Constructor:
  - ToolCall(string Name, string ArgumentsJson)

### Record: VisionImage
> OpenAI-compatible chat completion request.
Constructor:
  - VisionImage(string MimeType, byte[] Data)
