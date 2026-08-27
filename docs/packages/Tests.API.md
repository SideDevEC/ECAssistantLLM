# Tests.API.md

Types: 24  |  LOC: 2125  |  ~1176 tokens

---

### Class: ChatCompletionTests
> Tests for OpenAI-compatible chat completions (streaming + non-streaming),
Constructor:
  - ChatCompletionTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ChatMessageContentConverterTests
> Multimodal content parsing: plain strings, content-part arrays, data-URI images.
Cross-package deps: ECAssistant.LLM.Models, Xunit

### Class: ClientAuthTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ClientManagementTests
> Tests for client registration, heartbeat and disconnect
Constructor:
  - ClientManagementTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ConcurrentRequestTests
> Tests for concurrent access to the LLM server.
Constructor:
  - ConcurrentRequestTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: EmbeddingsTests
> Tests for the OpenAI-compatible embeddings endpoint.
Constructor:
  - EmbeddingsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ErrorHandlingTests
> Cross-cutting error-handling tests: unknown paths, wrong methods,
Constructor:
  - ErrorHandlingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: HealthTests
> Tests for GET /eca/health.
Constructor:
  - HealthTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: HeartbeatTests
> Tests for heartbeat mechanism. Uses the main TestServerFixture (timeout 300s).
Constructor:
  - HeartbeatTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: KvCacheTests
> Tests for KV-cache operations: prefill, save-state, rewind, reset, and the
Constructor:
  - KvCacheTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: MalformedRequestTests
> Tests for malformed requests — valid JSON but wrong types, missing fields,
Constructor:
  - MalformedRequestTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ModelLoadTests
> Tests for runtime model management: /eca/models/load, /eca/models/unload.
Constructor:
  - ModelLoadTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ModelPathRestrictionTests
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ModelsTests
> Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
Constructor:
  - ModelsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: PrefixMathTests
> Pure-math tests for prompt-cache common-prefix bookkeeping.
Cross-package deps: LLama.Native, ECAssistant.LLM.Engine

### Class: RouteMethodTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: RoutingTests
> Tests for client routing and session namespacing:
Constructor:
  - RoutingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SecurityHarness
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ServerCollection
> Shared collection so all integration tests use the single
Implements: TestServerFixture>
Cross-package deps: Xunit

### Class: ServerLogTests
> Tests that the server creates log files and writes meaningful entries
Constructor:
  - ServerLogTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: SessionLifecycleTests
> Tests for session lifecycle: create, status, destroy, and validation.
Constructor:
  - SessionLifecycleTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ShutdownTests
> Tests for the /eca/shutdown endpoint.
Constructor:
  - ShutdownTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: TestServerFixture
> Shared integration-test fixture. Starts the real ECAssistantLLM HTTP server
Implements: IAsyncLifetime
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: TokenizeTests
> Tests for the ECA tokenize endpoint (POST /eca/tokenize).
Constructor:
  - TokenizeTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures
