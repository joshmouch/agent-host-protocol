#!/usr/bin/env bash
# AHP .NET Conformance Corpus Runner — build-phase B5.
#
# Runs the full scenario corpus (round-trips + reducers + negatives) through
# the REAL .NET client reducer against the REAL scenario-driven host.
# No mocks. Real WebSocket. Real reducers. Real assertions.
#
# Usage:
#   conformance/run-corpus.sh [--verbose] [--filter <prefix>] [--dotnet-args ...]
#
# Expected tail (all 233 pass):
#   Results: 233/233 passed, 0 failed, 0 errored — <N>s
set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$DIR/.." && pwd)
TS_CLIENT="$REPO/clients/typescript"
HOST_DIR="$DIR/host"
RUNNER_DIR="$DIR/dotnet/CorpusRunner"

# --- 1. TypeScript client: scenario host needs clients/typescript built.
if [ ! -f "$TS_CLIENT/dist/types/index.js" ]; then
  echo "building in-repo TypeScript client (clients/typescript)…"
  [ -d "$REPO/node_modules" ]            || (cd "$REPO"       && npm install --no-audit --no-fund >/dev/null)
  [ -f "$TS_CLIENT/src/types/index.ts" ] || (cd "$REPO"       && npm run generate:typescript      >/dev/null)
  [ -d "$TS_CLIENT/node_modules" ]       || (cd "$TS_CLIENT"  && npm install --no-audit --no-fund >/dev/null)
  (cd "$TS_CLIENT" && npm run build >/dev/null)
fi

# --- 2. Conformance host: install deps (the scenario host needs ws + TS client).
echo "installing conformance host deps…"
(cd "$HOST_DIR" && npm install --no-audit --no-fund >/dev/null)

# --- 3. .NET CorpusRunner: build (pulls in ProjectReference client chain).
echo "building .NET CorpusRunner (Release/net8.0)…"
dotnet build "$RUNNER_DIR" -c Release -f net8.0 >/dev/null

# --- 4. Run the corpus.
echo "running corpus…"
dotnet run --project "$RUNNER_DIR" -c Release -f net8.0 --no-build -- \
  --host "$HOST_DIR/scenario-host.mjs" \
  --scenarios "$REPO/types/test-cases/scenarios" \
  "$@"
