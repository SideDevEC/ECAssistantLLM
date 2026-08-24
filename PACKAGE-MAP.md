# PACKAGE-MAP.md — ECAssistantLLM

Generated: 2026-08-24T20:39:04.104170+00:00

| Package | Types | LOC | ~Tokens | Dependencies |
|---|---|---|---|---|
| Config | 5 | 123 | ~41 | — |
| Engine | 9 | 660 | ~220 | Config, Root |
| Models | 23 | 209 | ~69 | — |
| Root | 3 | 144 | ~48 | — |
| Server | 3 | 728 | ~242 | Config, Engine, Root |

## Dependency Direction

Config → (leaf, no deps)
Engine → Config, Root
Models → (leaf, no deps)
Root → (leaf, no deps)
Server → Config, Engine, Root
