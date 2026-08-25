# ECAssistantLLM.API.md

Types: 43  |  LOC: 1935  |  ~1451 tokens

---

### Interface: ILogger
> Simple file + console logger for the LLM server.
Methods:
  - void Info(string tag, string message)
  - void Warn(string tag, string message)
  - void Error(string tag, string message)
  - void Debug(string tag, string message)

### Class: ChatCompletionChunk
> SSE streaming chunk (OpenAI format).

### Class: ChatCompletionRequest
> OpenAI-compatible chat completion request.

### Class: ChatMessage
> OpenAI-compatible chat completion request.

### Class: ChunkChoice
> SSE streaming chunk (OpenAI format).

### Class: ChunkDelta
> SSE streaming chunk (OpenAI format).

### Class: ClientManager
> Manages client connections: registration, heartbeat, eviction.
Constructor:
  - ClientManager(SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, SessionRegistry sessionRegistry, ECAssistant.LLM.Config.LlmServerConfig config, ILogger logger, Action? onLastClientDisconnected)

### Class: ClientRegisterRequest
> Generic API error response.

### Class: ClientRegisterResponse
> Generic API error response.

### Class: CreateSessionRequest
> Generic API error response.

### Class: EmbeddingData

### Class: EmbeddingRequest

### Class: EmbeddingResponse

### Class: ErrorDetail
> Generic API error response.

### Class: ErrorResponse
> Generic API error response.

### Class: HeartbeatRequest
> Generic API error response.

### Class: HeartbeatResponse
> Generic API error response.

### Class: InferenceDefaults
> Default inference parameters. Can be overridden per request.

### Class: InferenceScheduler
> Serializes inference across all clients.
Constructor:
  - InferenceScheduler(ILogger logger)

### Class: LlmHttpServer
> Main HTTP server using HttpListener. Routes requests to OpenAI and ECAssistant endpoints.
Implements: IDisposable
Constructor:
  - LlmHttpServer(LlmServerConfig config, MultiModelHost modelHost, SessionRegistry sessionRegistry, InferenceScheduler scheduler, VramBudget vramBudget, ClientManager clientManager, ILogger logger, CancellationTokenSource? externalCts = null)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Models, ECAssistant.LLM.Server

### Class: LlmServerConfig
> Root server configuration. Deserialized from llm-server.json.

### Class: LoadModelRequest
> Generic API error response.

### Class: LoggingSection
> Logging configuration for the server.

### Class: ModelConfig
> Configuration for a single model hosted by the server.

### Class: ModelSlot
> One loaded model: weights + params + status.
Implements: IDisposable
Constructor:
  - ModelSlot(string id, ModelConfig config, ILogger logger)
Cross-package deps: LLama, LLama.Common, LLama.Native, ECAssistant.LLM.Config

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

### Class: RequestRouter
> Routes incoming HTTP requests to the appropriate handler.
Constructor:
  - RequestRouter(MultiModelHost models, SessionRegistry sessions, InferenceScheduler scheduler, VramBudget vram, ClientManager clients, LlmServerConfig config, ILogger logger, CancellationTokenSource cts)
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Models

### Class: RewindRequest
> Generic API error response.

### Class: RewindResponse
> Generic API error response.

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
  - SessionContext(string clientId, string sessionId, string modelId, LLamaWeights weights, ModelParams modelParams, InferenceParams inferenceParams, ILogger logger)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config

### Class: SessionRegistry
> Registry of all client sessions across the server.
Implements: IDisposable
Constructor:
  - SessionRegistry(MultiModelHost modelHost, InferenceScheduler scheduler, LlmServerConfig config, ILogger logger)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config

### Class: SseStreamer
> Writes SSE (Server-Sent Events) streaming responses for OpenAI-compatible chat completions.
Cross-package deps: ECAssistant.LLM.Models

### Class: SuccessResponse
> Generic API error response.

### Class: TokenizeRequest

### Class: TokenizeResponse

### Class: VramBudget
> Tracks total VRAM usage across all sessions.
Constructor:
  - VramBudget(LlmServerConfig config)
Cross-package deps: ECAssistant.LLM.Config

### Record: ClientInfo
> Manages client connections: registration, heartbeat, eviction.
Constructor:
  - ClientInfo(string Id, string Name, string Version, DateTime RegisteredAt, DateTime LastHeartbeat, int ActiveSessions)

### Record: ModelInfo
> Manages multiple loaded models (at least 2: main + embeddings).
Constructor:
  - ModelInfo(string Id, string Path, bool IsLoaded, bool IsEmbedding, int GpuLayers, uint ContextSize, int EmbeddingDim)
Cross-package deps: LLama, ECAssistant.LLM.Config

### Record: SessionStatusInfo
> Registry of all client sessions across the server.
Constructor:
  - SessionStatusInfo(string ClientId, string SessionId, string ModelId, bool IsPrefilled, int ApproxTokenCount, uint ContextSize, double EstimatedVramMb, DateTime CreatedAt, DateTime LastActivity)
Cross-package deps: LLama, LLama.Common, LLama.Sampling, ECAssistant.LLM.Config
