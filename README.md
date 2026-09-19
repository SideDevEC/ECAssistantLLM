# ECAssistantLLM

> **Local inference for ECAssistant — and for anything that speaks OpenAI.** A self-contained, OpenAI-compatible LLM server powered by LLamaSharp. Your models, your machine, your keys — chat stays on `localhost`.

[![NuGet](https://img.shields.io/nuget/v/ECAssistant.LLM.Server)](https://www.nuget.org/packages/ECAssistant.LLM.Server)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![LLamaSharp](https://img.shields.io/badge/LLamaSharp-0.27.0-green.svg)](https://github.com/SciSharp/LLamaSharp)

ECAssistant exists to **assist** people — and assistance you can't trust leaks nothing. This server is the privacy cornerstone of [ECAssistant](https://github.com/SideDevEC/ECAssistant): it runs locally, serves multiple apps at once, and shuts down when the last client leaves. It's also a standalone product — point any OpenAI-compatible client at it and go.

---

## Overview

ECAssistantLLM is a self-contained HTTP server that wraps [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (a C# binding for [llama.cpp](https://github.com/ggerganov/llama.cpp)) behind an OpenAI-compatible API. It runs locally on your machine, serving inference requests from ECAssistant-flavored applications and any other client that speaks the OpenAI Chat Completions protocol.

## Features

- **OpenAI-compatible API** — drop-in replacement for `api.openai.com` for local inference
- **Multi-client support** — multiple ECAssistant applications share one server instance
- **Automatic lifecycle** — server starts on demand, shuts down when the last client disconnects
- **KV cache management** — per-session prefix caching with rewind support
- **Streaming (SSE)** — real-time token streaming via Server-Sent Events
- **Grammar-constrained decoding** — GBNF grammar forces valid JSON output with early termination
- **Multi-model hosting** — chat + embedding models loaded simultaneously with VRAM budgeting
- **Vision support** — multimodal models with MTMD/mmproj image understanding
- **Cross-platform** — macOS (arm64), Linux (x64), Windows (x64) via LLamaSharp native backends
- **Tokenization** — `/eca/tokenize` endpoint for exact token counting

## Architecture

```
┌──────────────────┐     HTTP/SSE      ┌──────────────────┐
│  ECAssistantCore │ ◄──────────────► │  ECAssistantLLM  │
│  (or any client) │   localhost:48217 │  (this server)   │
└──────────────────┘                   └────────┬─────────┘
                                                │
                                       ┌────────▼─────────┐
                                       │   LLamaSharp     │
                                       │  (llama.cpp)     │
                                       └────────┬─────────┘
                                                │
                                       ┌────────▼─────────┐
                                       │   GGUF Models    │
                                       │  ~/.ECAssistant  │
                                       │  LLM/models/     │
                                       └──────────────────┘
```

### Key Components

| Component | Description |
|-----------|-------------|
| `LlmHttpServer` | HTTP listener, request routing, SSE streaming |
| `RequestRouter` | OpenAI-compatible + `/eca/*` extension endpoints |
| `MultiModelHost` | Manages loaded models, GPU layers, VRAM budget |
| `InferenceScheduler` | Queues and dispatches inference requests |
| `SessionRegistry` | Per-client KV cache sessions with prompt caching |
| `ProcessModelHost` | Child llama-server supervision for ternary/external models (Bonsai) |
| `ProcessSessionRegistry` | Transcript-backed sessions for process models — same client-facing behavior as KV sessions |
| `ClientManager` | Client registration, heartbeat, last-client shutdown |
| `StructuredDecoder` | GBNF grammar-constrained JSON decoding |
| `VramBudget` | GPU memory budget enforcement across models |

## API Endpoints

### OpenAI-compatible

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/chat/completions` | Chat completions (streaming + non-streaming) |
| `POST` | `/v1/completions` | Text completions |
| `POST` | `/v1/embeddings` | Text embeddings |
| `GET` | `/v1/models` | List loaded models |
| `GET` | `/health` | Health check |

### ECA Extensions (`/eca/*`)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/eca/clients` | Register client, get `clientId` |
| `DELETE` | `/eca/clients/{id}` | Disconnect client |
| `POST` | `/eca/sessions` | Create KV cache session |
| `DELETE` | `/eca/sessions/{id}` | Destroy KV cache session |
| `POST` | `/eca/tokenize` | Tokenize text (exact token count) |
| `POST` | `/eca/shutdown` | Graceful shutdown (last client wins) |

## Quick Start

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A GGUF model file (e.g. [Qwen](https://huggingface.co/Qwen), [Llama](https://huggingface.co/meta-llama))

### Build

```bash
git clone https://github.com/SideDevEC/ECAssistantLLM.git
cd ECAssistantLLM
dotnet build
```

### Run

```bash
dotnet run -- --root ~/.ECAssistantLLM
```

The server starts on `http://localhost:48217` and loads models defined in `~/.ECAssistantLLM/llm-server.json`.

### Configuration

Create `~/.ECAssistantLLM/llm-server.json`:

```json
{
  "server": {
    "host": "localhost",
    "port": 48217,
    "shutdown_on_last_client": true
  },
  "models": [
    {
      "id": "main",
      "path": "models/qwen3-35b.gguf",
      "gpu_layers": 99,
      "context_size": 32768,
      "threads": -1,
      "is_embedding": false
    },
    {
      "id": "embeddings",
      "path": "models/all-MiniLM-L6-v2.gguf",
      "gpu_layers": 0,
      "context_size": 2048,
      "is_embedding": true,
      "pooling_type": "mean"
    }
  ],
  "inference": {},
  "logging": {
    "level": "info",
    "file": "ecassistant-llm.log"
  }
}
```

## NuGet Package

The server ships on nuget.org for ECAssistant-flavored projects (and any project that wants a local OpenAI-compatible server staged into its build output):

```xml
<PackageReference Include="ECAssistant.LLM.Server" Version="14.7.8" />
```

The package contains the compiled server runtime as content files. NuGet places them in a `server/` directory in the consuming project's build output. The ECAssistant wizard copies these to `~/.ECAssistantLLM/server/` on first run — so nothing downloads at chat time.

### Publishing a new version

Zero-touch: tag and push, CI builds, packs, and publishes to nuget.org + GitHub Packages and creates a Release:

```bash
git tag llm-server-v14.7.9
git push origin llm-server-v14.7.9
```

## Dependencies

| Package | Version | License |
|---------|---------|---------|
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | 0.27.0 | MIT |
| [LLamaSharp.Backend.Cpu](https://github.com/SciSharp/LLamaSharp) | 0.27.0 | MIT |
| [LLamaSharp.Backend.Vulkan](https://github.com/SciSharp/LLamaSharp) | 0.27.0 | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 10.0.5 | MIT |

LLamaSharp wraps [llama.cpp](https://github.com/ggerganov/llama.cpp), which is also MIT licensed.

## The ecosystem

| Repo | What it is |
|---|---|
| [ECAssistant](https://github.com/SideDevEC/ECAssistant) | Start here — overview & docs |
| [ECAssistantCore](https://github.com/SideDevEC/ECAssistantCore) | The embeddable agent library (talks to this server over OpenAI-compatible HTTP only) |
| [ECAssistantTUI](https://github.com/SideDevEC/ECAssistantTUI) | Terminal UI library |
| [ECAssistantConsole](https://github.com/SideDevEC/ECAssistantConsole) | Reference host / end-user CLI |

## License

This project is licensed under the [MIT License](LICENSE).

Third-party components (Prism ML llama.cpp fork, Bonsai model weights, LLamaSharp) are
covered by their own licenses — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Copyright (c) 2026 SideDevEC