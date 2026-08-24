# RELATIONSHIP-GRAPH.md — ECAssistantLLM

Generated: 2026-08-24T20:39:04.096877+00:00
Types: 43  |  Implements edges: 1  |  Uses edges: 27

## Implements (class → interface)

- ServerLogger ──implements──► ILogger  [Root → Root]

## Uses (type → dependency)

- ClientManager ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- ClientManager ──uses──► SessionRegistry  [Engine → Engine]
- InferenceScheduler ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► ClientManager  [Server → Engine] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► ILogger  [Server → Root] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► InferenceScheduler  [Server → Engine] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► LlmServerConfig  [Server → Config] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► MultiModelHost  [Server → Engine] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► SessionRegistry  [Server → Engine] ⚠ CROSS-PKG
- LlmHttpServer ──uses──► VramBudget  [Server → Engine] ⚠ CROSS-PKG
- ModelSlot ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- ModelSlot ──uses──► ModelConfig  [Engine → Config] ⚠ CROSS-PKG
- MultiModelHost ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- MultiModelHost ──uses──► LlmServerConfig  [Engine → Config] ⚠ CROSS-PKG
- RequestRouter ──uses──► ClientManager  [Server → Engine] ⚠ CROSS-PKG
- RequestRouter ──uses──► ILogger  [Server → Root] ⚠ CROSS-PKG
- RequestRouter ──uses──► InferenceScheduler  [Server → Engine] ⚠ CROSS-PKG
- RequestRouter ──uses──► LlmServerConfig  [Server → Config] ⚠ CROSS-PKG
- RequestRouter ──uses──► MultiModelHost  [Server → Engine] ⚠ CROSS-PKG
- RequestRouter ──uses──► SessionRegistry  [Server → Engine] ⚠ CROSS-PKG
- RequestRouter ──uses──► VramBudget  [Server → Engine] ⚠ CROSS-PKG
- SessionContext ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- SessionRegistry ──uses──► ILogger  [Engine → Root] ⚠ CROSS-PKG
- SessionRegistry ──uses──► InferenceScheduler  [Engine → Engine]
- SessionRegistry ──uses──► LlmServerConfig  [Engine → Config] ⚠ CROSS-PKG
- SessionRegistry ──uses──► MultiModelHost  [Engine → Engine]
- VramBudget ──uses──► LlmServerConfig  [Engine → Config] ⚠ CROSS-PKG

## Cross-Package Dependencies

- Engine → Config, Root
- Server → Config, Engine, Root
