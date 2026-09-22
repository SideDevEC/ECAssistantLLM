# RELATIONSHIP-GRAPH.md — ECAssistantLLM

Generated: 2026-09-22T06:30:51.220147+00:00
Edges: 27  |  Packages: 2

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
- ProcessModelHost ──implements──► IProcessModelHost (ECAssistantLLM)
- ProcessModelHost ──uses──► ILogger (ECAssistantLLM)
- ProcessModelInstance ──uses──► ILogger (ECAssistantLLM)
- ProcessSession ──uses──► ILogger (ECAssistantLLM)
- ProcessSession ──uses──► IProcessModelHost (ECAssistantLLM)
- ProcessSessionRegistry ──uses──► ILogger (ECAssistantLLM)
- ProcessSessionRegistry ──uses──► IProcessModelHost (ECAssistantLLM)
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
