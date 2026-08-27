# ECAssistantLLM Test Suites

Two tiers:

| Tier | Command | Loads GGUF models | Runtime |
|---|---|---|---|
| Unit / Security | `scripts/run-unit-tests.sh` | ❌ | seconds |
| Full-system E2E | `scripts/run-e2e.sh [ClassNameFilter]` | ✅ (~6 GB RAM, minutes) | several minutes |

E2E classes carry `[Trait("Category","E2E")]` and spin the real HTTP server
(`TestServerFixture`) with qwen3-8b + MiniLM from `bin/Debug/net8.0/models/`.

Rules:
- Never run the E2E tier inside interactive agent sessions — long-running,
  memory-heavy; use the script with a watchdog instead.
- If a run dies with "Test Run Aborted", cleanup already handles zombie
  testhosts — just re-run the script.
