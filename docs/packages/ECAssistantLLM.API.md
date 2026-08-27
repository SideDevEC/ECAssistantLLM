# ECAssistantLLM.API.md

Types: 80  |  LOC: 4949  |  ~3288 tokens

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
  - IReadOnlyList<ClientInfo> ListClients()
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

### Interface: IRequestRouter
> Interface for routing incoming HTTP requests to the appropriate handler.
Methods:
  - Task RouteAsync(HttpListenerContext ctx, CancellationToken ct)

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
  - ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, Action? onLastClientDisconnected)
Cross-package deps: ECAssistant.LLM.Interfaces

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

### Class: KvCacheTests
> Tests for KV-cache operations: prefill, save-state, rewind, reset, and the
Constructor:
  - KvCacheTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: LlmHttpServer
> Main HTTP server using HttpListener. Routes requests to OpenAI and ECAssistant endpoints.
Implements: IDisposable
Constructor:
  - LlmHttpServer(LlmServerConfig config, MultiModelHost modelHost, SessionRegistry sessionRegistry, IInferenceScheduler scheduler, VramBudget vramBudget, IClientManager clientManager, ILogger logger, CancellationTokenSource? externalCts = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Interfaces, ECAssistant.LLM.Models, ECAssistant.LLM.Server

### Class: LlmServerConfig
> Root server configuration. Deserialized from llm-server.json.

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

### Class: ModelPathRestrictionTests
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ModelSlot
> One loaded model: weights + params + status.
Implements: IDisposable
Constructor:
  - ModelSlot(string id, ModelConfig config, ILogger logger)
Cross-package deps: LLama, LLama.Common, LLama.Native, ECAssistant.LLM.Config

### Class: ModelsTests
> Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
Constructor:
  - ModelsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: MultiModelHost
> Manages multiple loaded models (at least 2: main + embeddings).
Implements: IDisposable
Constructor:
  - MultiModelHost(LlmServerConfig config, ILogger logger)
Cross-package deps: LLama, ECAssistant.LLM.Config

### Class: PrefillRequest
> Generic API error response.

### Class: PrefillResponse
> Generic API error response.

### Class: PrefixMath
> Pure helpers for prompt-cache bookkeeping.
Cross-package deps: LLama.Native

### Class: PrefixMathTests
> Pure-math tests for prompt-cache common-prefix bookkeeping.
Cross-package deps: LLama.Native, ECAssistant.LLM.Engine

### Class: PromptCacheSession
> Persistent prompt-cache session reusing warm KV state across stateless/background calls
Implements: IDisposable
Constructor:
  - PromptCacheSession(string modelId, LLamaWeights weights, ModelParams modelParams, ILogger logger)
Cross-package deps: LLama, LLama.Common

### Class: PromptCacheSessionManager
> Owns one <see cref="PromptCacheSession"/> per model slot (lazily created).
Implements: IDisposable
Constructor:
  - PromptCacheSessionManager(MultiModelHost models, ILogger logger)
Cross-package deps: LLama.Common

### Class: RequestRouter
> Routes incoming HTTP requests to the appropriate handler.
Implements: IRequestRouter
Constructor:
  - RequestRouter(MultiModelHost models, SessionRegistry sessions, IInferenceScheduler scheduler, VramBudget vram, IClientManager clients, LlmServerConfig config, ILogger logger, CancellationTokenSource cts)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Interfaces, ECAssistant.LLM.Models

### Class: RewindRequest
> Generic API error response.

### Class: RewindResponse
> Generic API error response.

### Class: RouteMethodTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: RoutingTests
> Tests for client routing and session namespacing:
Constructor:
  - RoutingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SecurityHarness
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ServerCollection
> Shared collection so all integration tests use the single
Implements: TestServerFixture>
Cross-package deps: Xunit

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
> Writes SSE (Server-Sent Events) streaming responses for OpenAI-compatible chat completions.
Cross-package deps: ECAssistant.LLM.Models

### Class: SuccessResponse
> Generic API error response.

### Class: TestServerFixture
> Shared integration-test fixture. Starts the real ECAssistantLLM HTTP server
Implements: IAsyncLifetime
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: TokenizeRequest

### Class: TokenizeResponse

### Class: TokenizeTests
> Tests for the ECA tokenize endpoint (POST /eca/tokenize).
Constructor:
  - TokenizeTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: VramBudget
> Tracks total VRAM usage across all sessions.
Constructor:
  - VramBudget(LlmServerConfig config)
Cross-package deps: ECAssistant.LLM.Config

### Record: ClientInfo
> Manages client connections: registration, heartbeat, eviction.
Constructor:
  - ClientInfo(string Id, string Name, string Version, DateTime RegisteredAt, DateTime LastHeartbeat, int ActiveSessions)
Cross-package deps: ECAssistant.LLM.Interfaces

### Record: ModelInfo
> Manages multiple loaded models (at least 2: main + embeddings).
Constructor:
  - ModelInfo(string Id, string Path, bool IsLoaded, bool IsEmbedding, int GpuLayers, uint ContextSize, int EmbeddingDim)
Cross-package deps: LLama, ECAssistant.LLM.Config

### Record: SessionStatusInfo
> Registry of all client sessions across the server.
Constructor:
  - SessionStatusInfo(string ClientId, string SessionId, string ModelId, bool IsPrefilled, int ApproxTokenCount, uint ContextSize, double EstimatedVramMb, DateTime CreatedAt, DateTime LastActivity)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config, ECAssistant.LLM.Interfaces

### Record: VisionImage
> OpenAI-compatible chat completion request.
Constructor:
  - VisionImage(string MimeType, byte[] Data)
