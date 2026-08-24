# API-INDEX.md — ECAssistantLLM

Generated: 2026-08-24T20:39:04.084356+00:00
Packages: 5  |  Types: 43  |  LOC: 1864

## Config (5 types, ~123 LOC)

- 🟡 InferenceDefaults  (ECAssistant.LLM.Config)
- 🟡 LlmServerConfig  (ECAssistant.LLM.Config)
- 🟡 LoggingSection  (ECAssistant.LLM.Config)
- 🟡 ModelConfig  (ECAssistant.LLM.Config)
- 🟡 ServerSection  (ECAssistant.LLM.Config)

## Engine (9 types, ~792 LOC)

- 🟡 ClientManager  (ECAssistant.LLM.Engine)  deps: [SessionRegistry, ECAssistant.LLM.Config.LlmServerConfig, ILogger]
- 🟡 ClientRecord  (ECAssistant.LLM.Engine)  deps: [DateTime]
- 🟡 InferenceReleaser  (ECAssistant.LLM.Engine)  deps: [SemaphoreSlim]
- 🟡 InferenceScheduler  (ECAssistant.LLM.Engine)  deps: [ILogger]
- 🟡 ModelSlot  (ECAssistant.LLM.Engine)  deps: [ModelConfig, ILogger]
- 🟡 MultiModelHost  (ECAssistant.LLM.Engine)  deps: [LlmServerConfig, ILogger]
- 🟡 SessionContext  (ECAssistant.LLM.Engine)  deps: [LLamaWeights, ModelParams, InferenceParams, ILogger]
- 🟡 SessionRegistry  (ECAssistant.LLM.Engine)  deps: [MultiModelHost, InferenceScheduler, LlmServerConfig, ILogger]
- 🟡 VramBudget  (ECAssistant.LLM.Engine)  deps: [LlmServerConfig]

## Models (23 types, ~1529 LOC)

- 🟡 ChatCompletionChunk  (ECAssistant.LLM.Models)
- 🟡 ChatCompletionRequest  (ECAssistant.LLM.Models)
- 🟡 ChatMessage  (ECAssistant.LLM.Models)
- 🟡 ChunkChoice  (ECAssistant.LLM.Models)
- 🟡 ChunkDelta  (ECAssistant.LLM.Models)
- 🟡 ClientRegisterRequest  (ECAssistant.LLM.Models)
- 🟡 ClientRegisterResponse  (ECAssistant.LLM.Models)
- 🟡 CreateSessionRequest  (ECAssistant.LLM.Models)
- 🟡 EmbeddingData  (ECAssistant.LLM.Models)
- 🟡 EmbeddingRequest  (ECAssistant.LLM.Models)
- 🟡 EmbeddingResponse  (ECAssistant.LLM.Models)
- 🟡 ErrorDetail  (ECAssistant.LLM.Models)
- 🟡 ErrorResponse  (ECAssistant.LLM.Models)
- 🟡 HeartbeatRequest  (ECAssistant.LLM.Models)
- 🟡 HeartbeatResponse  (ECAssistant.LLM.Models)
- 🟡 LoadModelRequest  (ECAssistant.LLM.Models)
- 🟡 PrefillRequest  (ECAssistant.LLM.Models)
- 🟡 PrefillResponse  (ECAssistant.LLM.Models)
- 🟡 RewindRequest  (ECAssistant.LLM.Models)
- 🟡 RewindResponse  (ECAssistant.LLM.Models)
- 🟡 SuccessResponse  (ECAssistant.LLM.Models)
- 🟡 TokenizeRequest  (ECAssistant.LLM.Models)
- 🟡 TokenizeResponse  (ECAssistant.LLM.Models)

## Root (3 types, ~180 LOC)

- 🟡 ServerLogger : ILogger  (ECAssistant.LLM)  deps: [=, =]
- ⚪ LogLevel  (ECAssistant.LLM)
- 🔵 ILogger  (ECAssistant.LLM)

## Server (3 types, ~728 LOC)

- 🟡 LlmHttpServer  (ECAssistant.LLM.Server)  deps: [LlmServerConfig, MultiModelHost, SessionRegistry, InferenceScheduler, VramBudget, ClientManager, ILogger]
- 🟡 RequestRouter  (ECAssistant.LLM.Server)  deps: [MultiModelHost, SessionRegistry, InferenceScheduler, VramBudget, ClientManager, LlmServerConfig, ILogger]
- 🟡 SseStreamer  (ECAssistant.LLM.Server)
