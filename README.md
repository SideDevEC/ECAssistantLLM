# ECAssistant.LLM.Server

Standalone OpenAI-compatible local LLM server powered by LLamaSharp.

## What This Is

This NuGet package contains the compiled ECAssistant LLM server runtime (DLLs + LLamaSharp backends). It is consumed by ECAssistant-flavored projects (ECAssistantConsole, ECSQL, etc.) and installed to a shared standalone location at `~/.ECAssistantLLM/server/` by the first-run wizard.

## How It Works

1. Add `<PackageReference Include="ECAssistant.LLM.Server" Version="x.y.z" />` to your project
2. NuGet places `server/` content files in your build output
3. The ECAssistant wizard copies these to `~/.ECAssistantLLM/server/` on first run
4. All ECAssistant-flavored projects on the machine share the same server instance on port 48217
5. Server shuts down automatically when the last client disconnects

## Package Contents

- `ECAssistant.LLM.dll` — server executable (launched via `dotnet exec`)
- `LLamaSharp.dll` + backend DLLs — inference engine
- `runtimes/` — native libraries per platform (osx-arm64, linux-x64, win-x64)

## Requirements

- .NET 8.0 runtime
- GGUF model files (downloaded by the wizard from HuggingFace)