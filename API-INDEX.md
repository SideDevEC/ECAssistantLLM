# API-INDEX.md — ECAssistantLLM

Generated: 2026-08-25T21:22:38.587849+00:00
Packages: 1  |  Types: 48

---

## ECAssistantLLM (48 types, ~2180 LOC)

- 🔵 ILogger  (ECAssistantLLM)
- 🟡 ChatCompletionChunk  (ECAssistantLLM)
- 🟡 ChatCompletionRequest  (ECAssistantLLM)
- 🟡 ChatMessage  (ECAssistantLLM)
- 🟡 ChunkChoice  (ECAssistantLLM)
- 🟡 ChunkDelta  (ECAssistantLLM)
- 🟣 ClientInfo  (ECAssistantLLM)  deps: [string, string, string, DateTime, DateTime, int]
- 🟡 ClientManager  (ECAssistantLLM)  deps: [SessionRegistry, LlmServerConfig, ILogger, SessionRegistry, LlmServerConfig, ILogger, Action]
- 🟡 ClientRegisterRequest  (ECAssistantLLM)
- 🟡 ClientRegisterResponse  (ECAssistantLLM)
- 🟡 CompletionChoice  (ECAssistantLLM)
- 🟡 CompletionChunk  (ECAssistantLLM)
- 🟡 CompletionChunkChoice  (ECAssistantLLM)
- 🟡 CompletionRequest  (ECAssistantLLM)
- 🟡 CompletionResponse  (ECAssistantLLM)
- 🟡 CreateSessionRequest  (ECAssistantLLM)
- 🟡 EmbeddingData  (ECAssistantLLM)
- 🟡 EmbeddingRequest  (ECAssistantLLM)
- 🟡 EmbeddingResponse  (ECAssistantLLM)
- 🟡 ErrorDetail  (ECAssistantLLM)
- 🟡 ErrorResponse  (ECAssistantLLM)
- 🟡 HeartbeatRequest  (ECAssistantLLM)
- 🟡 HeartbeatResponse  (ECAssistantLLM)
- 🟡 InferenceDefaults  (ECAssistantLLM)
- 🟡 InferenceScheduler  (ECAssistantLLM)  deps: [ILogger]
- 🟡 LlmHttpServer : IDisposable  (ECAssistantLLM)  deps: [LlmServerConfig, MultiModelHost, SessionRegistry, InferenceScheduler, VramBudget, ClientManager, ILogger, CancellationTokenSource? externalCts =]
- 🟡 LlmServerConfig  (ECAssistantLLM)
- 🟡 LoadModelRequest  (ECAssistantLLM)
- 🟡 LoggingSection  (ECAssistantLLM)
- 🟡 ModelConfig  (ECAssistantLLM)
- 🟣 ModelInfo  (ECAssistantLLM)  deps: [string, string, bool, bool, int, uint, int]
- 🟡 ModelSlot : IDisposable  (ECAssistantLLM)  deps: [string, ModelConfig, ILogger]
- 🟡 MultiModelHost : IDisposable  (ECAssistantLLM)  deps: [LlmServerConfig, ILogger]
- 🟡 PrefillRequest  (ECAssistantLLM)
- 🟡 PrefillResponse  (ECAssistantLLM)
- 🟡 RequestRouter  (ECAssistantLLM)  deps: [MultiModelHost, SessionRegistry, InferenceScheduler, VramBudget, ClientManager, LlmServerConfig, ILogger, CancellationTokenSource]
- 🟡 RewindRequest  (ECAssistantLLM)
- 🟡 RewindResponse  (ECAssistantLLM)
- 🟡 ServerLogger : ILogger  (ECAssistantLLM)  deps: [LogLevel minLevel =, string? logFile =]
- 🟡 ServerSection  (ECAssistantLLM)
- 🟡 SessionContext : IDisposable  (ECAssistantLLM)  deps: [string, string, string, LLamaWeights, ModelParams, InferenceParams, ILogger]
- 🟡 SessionRegistry : IDisposable  (ECAssistantLLM)  deps: [MultiModelHost, InferenceScheduler, LlmServerConfig, ILogger]
- 🟣 SessionStatusInfo  (ECAssistantLLM)  deps: [string, string, string, bool, int, uint, double, DateTime, DateTime]
- 🟡 SseStreamer  (ECAssistantLLM)
- 🟡 SuccessResponse  (ECAssistantLLM)
- 🟡 TokenizeRequest  (ECAssistantLLM)
- 🟡 TokenizeResponse  (ECAssistantLLM)
- 🟡 VramBudget  (ECAssistantLLM)  deps: [LlmServerConfig]
