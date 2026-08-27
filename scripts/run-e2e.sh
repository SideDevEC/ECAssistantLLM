#!/usr/bin/env bash
# Full-system E2E run for ECAssistantLLM.
# Loads the real GGUF models (qwen3-8b + MiniLM) into the test process —
# expect several minutes of runtime and ~6 GB RAM.
#
# Usage:
#   scripts/run-e2e.sh            # all E2E tests
#   scripts/run-e2e.sh Health     # single substring filter (e.g. class name)
#
# NOTE: run OUTSIDE interactive sessions with a soft deadline:
#   bash scripts/run-e2e.sh &  then poll/kill as needed.
export DOTNET_NODE_REUSE=false
cd "$(dirname "$0")/.." || exit 1

MODELS=bin/Debug/net8.0/models
MAIN_GGUF="$MODELS/qwen3-8b-q4_k_m.gguf"
EMB_GGUF="$MODELS/all-MiniLM-L6-v2-q4_k_m.gguf"

for f in "$MAIN_GGUF" "$EMB_GGUF"; do
    if [ ! -f "$f" ]; then
        echo "❌ Missing model: $f"
        echo "   The E2E suite requires real GGUF files in bin/Debug/net8.0/models/"
        exit 2
    fi
done

EXTRA=${1:+--filter "FullyQualifiedName~$1"}

echo "── Building ──"
dotnet build Tests/ECAssistantLLM.Tests.csproj -v q --nologo | grep -E ": error " && exit 1

echo "── Running E2E (Category=E2E) ${1:-all} ──"
dotnet test Tests/ECAssistantLLM.Tests.csproj --nologo --filter "Category=E2E" $EXTRA
rc=$?

echo "── Cleanup ──"
pkill -f testhost 2>/dev/null
pkill -f MSBuild.dll 2>/dev/null

if [ $rc -eq 0 ]; then echo "✅ E2E PASSED"; else echo "❌ E2E FAILED (rc=$rc)"; fi
exit $rc
