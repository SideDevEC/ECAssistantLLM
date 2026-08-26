# API-INDEX.md — ECAssistantLLM

Generated: 2026-08-26T11:58:23.122053+00:00
Packages: 1  |  Types: 51

---

## ECAssistantLLM (51 types, ~2291 LOC)

- 🔵 IClientManager  (ECAssistantLLM)
- 🔵 IInferenceScheduler  (ECAssistantLLM)
- 🔵 ILogger  (ECAssistantLLM)
- 🔵 IRequestRouter  (ECAssistantLLM)
- 🟡 ChatCompletionChunk  (ECAssistantLLM)
- 🟡 ChatCompletionRequest  (ECAssistantLLM)
- 🟡 ChatMessage  (ECAssistantLLM)
- 🟡 ChunkChoice  (ECAssistantLLM)
- 🟡 ChunkDelta  (ECAssistantLLM)
- 🟣 ClientInfo  (ECAssistantLLM)  deps: [string, string, string, DateTime, DateTime, int]
- 🟡 ClientManager : IClientManager, IDisposable  (ECAssistantLLM)  deps: [SessionRegistry, LlmServerConfig, ILogger, SessionRegistry, LlmServerConfig, ILogger, Action]
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
- 🟡 InferenceScheduler : IInferenceScheduler  (ECAssistantLLM)  deps: [ILogger]
- 🟡 LlmHttpServer : IDisposable  (ECAssistantLLM)  deps: [LlmServerConfig, MultiModelHost, SessionRegistry, IInferenceScheduler, VramBudget, IClientManager, ILogger, CancellationTokenSource? externalCts =]
- 🟡 LlmServerConfig  (ECAssistantLLM)
- 🟡 LoadModelRequest  (ECAssistantLLM)
- 🟡 LoggingSection  (ECAssistantLLM)
- 🟡 ModelConfig  (ECAssistantLLM)
- 🟣 ModelInfo  (ECAssistantLLM)  deps: [string, string, bool, bool, int, uint, int]
- 🟡 ModelSlot : IDisposable  (ECAssistantLLM)  deps: [string, ModelConfig, ILogger]
- 🟡 MultiModelHost : IDisposable  (ECAssistantLLM)  deps: [LlmServerConfig, ILogger]
- 🟡 PrefillRequest  (ECAssistantLLM)
- 🟡 PrefillResponse  (ECAssistantLLM)
- 🟡 RequestRouter : IRequestRouter  (ECAssistantLLM)  deps: [MultiModelHost, SessionRegistry, IInferenceScheduler, VramBudget, IClientManager, LlmServerConfig, ILogger, CancellationTokenSource]
- 🟡 RewindRequest  (ECAssistantLLM)
- 🟡 RewindResponse  (ECAssistantLLM)
- 🟡 ServerLogger : ILogger  (ECAssistantLLM)  deps: [LogLevel minLevel =, string? logFile =]
- 🟡 ServerSection  (ECAssistantLLM)
- 🟡 SessionContext : IDisposable  (ECAssistantLLM)  deps: [string, string, string, LLamaWeights, ModelParams, InferenceParams, ILogger]
- 🟡 SessionRegistry : IDisposable  (ECAssistantLLM)  deps: [MultiModelHost, IInferenceScheduler, LlmServerConfig, ILogger]
- 🟣 SessionStatusInfo  (ECAssistantLLM)  deps: [string, string, string, bool, int, uint, double, DateTime, DateTime]
- 🟡 SseStreamer  (ECAssistantLLM)
- 🟡 SuccessResponse  (ECAssistantLLM)
- 🟡 TokenizeRequest  (ECAssistantLLM)
- 🟡 TokenizeResponse  (ECAssistantLLM)
- 🟡 VramBudget  (ECAssistantLLM)  deps: [LlmServerConfig]
