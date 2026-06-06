#!/usr/bin/env bash
# Run a single *.scenario.json file through the scenario-driven host and print
# the SCENARIO HOST READY line so callers can connect.
#
# Usage:
#   ./conformance/host/run-scenario.sh <path-to-scenario.json>
#
# The script:
#   1. Installs conformance/host deps (same as run.sh step 2).
#   2. Validates the scenario file with the dep-free validator.
#   3. Starts scenario-host.mjs with the given file; prints its READY line.
#
# Exits 0 when the host exits cleanly (client disconnected, plan exhausted).
# Exits non-zero on any failure.
set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$DIR/../.." && pwd)
TS_CLIENT="$REPO/clients/typescript"
SCENARIO_FILE="${1:-}"

if [ -z "$SCENARIO_FILE" ]; then
  echo "Usage: $0 <path-to-scenario.json>" >&2
  exit 1
fi
if [ ! -f "$SCENARIO_FILE" ]; then
  echo "Scenario file not found: $SCENARIO_FILE" >&2
  exit 1
fi

# --- 1. TypeScript client: build only if missing (same gate as run.sh).
if [ ! -f "$TS_CLIENT/dist/types/index.js" ]; then
  echo "building in-repo TypeScript client (clients/typescript)…"
  [ -d "$REPO/node_modules" ]       || (cd "$REPO" && npm install --no-audit --no-fund >/dev/null)
  [ -f "$TS_CLIENT/src/types/index.ts" ] || (cd "$REPO" && npm run generate:typescript >/dev/null)
  [ -d "$TS_CLIENT/node_modules" ]  || (cd "$TS_CLIENT" && npm install --no-audit --no-fund >/dev/null)
  (cd "$TS_CLIENT" && npm run build >/dev/null)
fi

# --- 2. Install conformance host deps.
echo "installing conformance host deps…"
(cd "$DIR" && npm install --no-audit --no-fund >/dev/null)

# --- 3. Validate the scenario file.
SCENARIOS_DIR="$REPO/types/test-cases/scenarios"
echo "validating scenario…"
node "$SCENARIOS_DIR/scripts/validate-scenarios.mjs" "$SCENARIO_FILE"

# --- 4. Start the scenario-driven host and stream its output.
echo "starting scenario host for: $SCENARIO_FILE"
node "$DIR/scenario-host.mjs" "$SCENARIO_FILE"
