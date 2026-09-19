# Tests.API.md

Types: 40  |  LOC: 3633  |  ~1878 tokens

---

### Class: BackendPortAllocatorTests
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends

### Class: BackendSelectorTests
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, Xunit

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

### Class: LlmServerConfigTests
> Config validation and JSON Save/Load round-trip tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config

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

### Class: ModelPathPolicyTests
> Pure-logic tests for model-path containment (models_root traversal guard).
Cross-package deps: ECAssistant.LLM.Server

### Class: ModelPathRestrictionTests
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ModelsTests
> Tests for the model catalog endpoints (GET /v1/models and GET /eca/models).
Constructor:
  - ModelsTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: ProcessOrphanReapTests
> Cross-platform orphan-proofing: PID files + safe reaping (binary-name guard so a
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Server, Xunit

### Class: ProcessSessionRegistryTests
> Stub host — never starts a real child; url resolution fails fast.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, Xunit

### Class: ProcessSessionStructuredTests
> Structured mode on process-backend models: grammar + enable_thinking=false reach
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: ProcessStatelessClientTests
> Stateless process inference: sampling parity (explicit in-process defaults, never
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine.Backends, ECAssistant.LLM.Models, ECAssistant.LLM.Server, Xunit

### Class: RouteMethodTests
> Lightweight server harness for auth/security tests.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: RoutingTests
> Tests for client routing and session namespacing:
Constructor:
  - RoutingTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: RuntimeLocatorTests
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: SecurityHarness
> Lightweight server harness for auth/security tests.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ServerCollection
> Shared collection so all integration tests use the single
Implements: TestServerFixture>
Cross-package deps: Xunit

### Class: ServerIdleShutdownTests
> Regression for the 2026-09-18/19 zombie: HttpListener.GetContextAsync is not
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server, Xunit

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

### Class: SseStreamerTests
> SSE framing tests over a real loopback HttpListener — no model weights involved.
Implements: IDisposable
Cross-package deps: ECAssistant.LLM.Server

### Class: StructuredDecoderTests
> v13 structured decision envelope decoding.
Cross-package deps: ECAssistant.LLM.Engine, Xunit

### Class: TernaryModelDetectorTests
Cross-package deps: ECAssistant.LLM.Engine.Backends, Xunit

### Class: TestServerFixture
> Shared integration-test fixture. Starts the real ECAssistantLLM HTTP server
Implements: IAsyncLifetime
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server

### Class: ThinkFilterTests
> v12.8 regression: reasoning models (Qwen3.5) leak &lt;think&gt;…&lt;/think&gt; blocks and
Cross-package deps: ECAssistant.LLM.Server, Xunit

### Class: TokenizeTests
> Tests for the ECA tokenize endpoint (POST /eca/tokenize).
Constructor:
  - TokenizeTests(TestServerFixture fixture)
Cross-package deps: ECAssistant.LLM.Tests.Fixtures

### Class: VisionImageSuiteE2ETests
> FULL-SYSTEM vision image suite (Category=E2E): runs against an EXTERNALLY
Implements: IAsyncLifetime
Cross-package deps: Xunit

### Class: VisionInferenceE2ETests
> FULL-SYSTEM vision E2E (Category=E2E, excluded from unit runs):
Cross-package deps: ECAssistant.LLM, ECAssistant.LLM.Config, ECAssistant.LLM.Engine, ECAssistant.LLM.Server, Xunit

### Class: VramBudgetTests
> Pure-logic tests for VRAM budget reserve/release/exceed accounting.
Cross-package deps: ECAssistant.LLM.Config, ECAssistant.LLM.Engine
