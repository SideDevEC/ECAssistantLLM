#!/usr/bin/env bash
# Fast unit-test run for ECAssistantLLM — excludes model-loading E2E suite.
export DOTNET_NODE_REUSE=false
cd "$(dirname "$0")/.." || exit 1

echo "── Building ──"
dotnet build Tests/ECAssistantLLM.Tests.csproj -v q --nologo | grep -E ": error " && exit 1

echo "── Unit tests (Category!=E2E) ──"
dotnet test Tests/ECAssistantLLM.Tests.csproj --nologo --filter "Category!=E2E"
rc=$?
pkill -f testhost 2>/dev/null; pkill -f MSBuild.dll 2>/dev/null
exit $rc
