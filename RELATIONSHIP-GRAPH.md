# RELATIONSHIP-GRAPH.md — ECAssistantLLM

Generated: 2026-08-26T11:58:23.122353+00:00
Edges: 18  |  Packages: 1

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
- RequestRouter ──implements──► IRequestRouter (ECAssistantLLM)
- RequestRouter ──uses──► IClientManager (ECAssistantLLM)
- RequestRouter ──uses──► IInferenceScheduler (ECAssistantLLM)
- RequestRouter ──uses──► ILogger (ECAssistantLLM)
- ServerLogger ──implements──► ILogger (ECAssistantLLM)
- SessionContext ──uses──► ILogger (ECAssistantLLM)
- SessionRegistry ──uses──► IInferenceScheduler (ECAssistantLLM)
- SessionRegistry ──uses──► ILogger (ECAssistantLLM)
