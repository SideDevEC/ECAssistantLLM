# RELATIONSHIP-GRAPH.md — ECAssistantLLM

Generated: 2026-08-28T07:47:33.393619+00:00
Edges: 22  |  Packages: 2

---

## ECAssistantLLM

- ClientManager ──implements──► IClientManager (ECAssistantLLM)
- ClientManager ──uses──► ILogger (ECAssistantLLM)
- ClientManager ──uses──► ILogger (ECAssistantLLM)
- InferenceScheduler ──implements──► IInferenceScheduler (ECAssistantLLM)
- InferenceScheduler ──uses──► ILogger (ECAssistantLLM)
- LlmHttpServer ──uses──► IClientManager (ECAssistantLLM)
- LlmHttpServer ──uses──► IInferenceScheduler (ECAssistantLLM)
- LlmHttpServer ──uses──► ILogger (ECAssistantLLM)
- ModelSlot ──uses──► ILogger (ECAssistantLLM)
- MultiModelHost ──uses──► ILogger (ECAssistantLLM)
- PromptCacheSession ──uses──► ILogger (ECAssistantLLM)
- PromptCacheSessionManager ──uses──► ILogger (ECAssistantLLM)
- RequestRouter ──implements──► IRequestRouter (ECAssistantLLM)
- RequestRouter ──uses──► IClientManager (ECAssistantLLM)
- RequestRouter ──uses──► IInferenceScheduler (ECAssistantLLM)
- RequestRouter ──uses──► ILogger (ECAssistantLLM)
- ServerLogger ──implements──► ILogger (ECAssistantLLM)
- SessionContext ──uses──► ILogger (ECAssistantLLM)
- SessionRegistry ──uses──► IInferenceScheduler (ECAssistantLLM)
- SessionRegistry ──uses──► IInferenceScheduler (ECAssistantLLM)
- SessionRegistry ──uses──► ILogger (ECAssistantLLM)
- SessionRegistry ──uses──► ILogger (ECAssistantLLM)

## Tests

- (no outgoing edges)
