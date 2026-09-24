# Contributing to ECAssistantLLM

Thank you for your interest in contributing!

## Ground Rules

- **Read-only by default:** This repository accepts contributions via pull requests only. Nothing merges without maintainer approval.
- **Stay anonymous-friendly:** Do not add personal names, emails, or identifying information anywhere in the codebase, docs, or commit messages. The project is maintained under the `SideDevEC` identity only.

## How to Contribute

1. **Fork** the repository
2. Create a feature branch from `main`:
   ```bash
   git checkout -b feature/my-change
   ```
3. Make your changes
4. **Build and test:**
   ```bash
   dotnet build
   dotnet test Tests/
   ```
5. Commit with a clear, descriptive message
6. Open a **Pull Request** against `main`

## Code Guidelines

- Strict object-oriented C# (.NET 8): interfaces, constructor injection, no static mutable state
- One type per file, file name matches type name
- Every class with behavior gets a matching `{ClassName}Tests.cs`
- No dev-environment paths (`bin/Debug`, `bin/Release`) referenced anywhere in code
- Keep `ARCHITECTURE.md` up to date with structural changes

## Pull Request Checklist

- [ ] Build passes with 0 errors
- [ ] Tests added/updated and passing
- [ ] No personal information committed
- [ ] `ARCHITECTURE.md` updated if the design changed
- [ ] Version bumped in `ECAssistant.LLM.csproj` if releasing

## Reporting Issues

Open a GitHub Issue with:
- What you expected vs. what happened
- Steps to reproduce
- Server log excerpt (`~/ECALLM/ecassistant-llm.log`) — **redact any sensitive paths or data first**
