#!/usr/bin/env bash
# AHP HOST-CONFORMANCE SUITE runner — build-phase B4, the end-to-end green proof.
#
# A scripted replay CLIENT (run-conformance.mjs) replays a TRANCHE of the B2
# scenario corpus against the real B3 scenario-driven host (../host/scenario-host.mjs)
# over a real WebSocket, applies every server.notify action through the CANONICAL
# in-repo reducers (clients/typescript, wired as a `file:` dependency), and checks
# every client.assert.* step. NO MOCKS — real files, real transport, real
# reducers, real assertions. (CROSS-SPEC-INTENT-VERIFIED-BY-REAL-EXECUTION + ADR-067/072.)
#
# Self-contained: every dependency resolves inside this repo (the TS client via a
# `file:` dependency, ws from npm). Mirrors ../run.sh's bootstrap discipline.
#
#   conformance/
#     host/    → scenario-host.mjs (B3, the scenario-driven host)
#     runner/  → run-conformance.mjs + conformance-suite.mjs + this script
#
# Expected tail:
#   HOST-CONFORMANCE SUITE PASS — N/N scenarios converge against the real host …
#
# Usage:
#   ./run.sh                  # default tranche (all round-trips + 30 reducer sample + all negatives)
#   ./run.sh --all-reducers   # run every reducer scenario (full corpus)
#   ./run.sh --verbose        # per-assertion detail
set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$DIR/../.." && pwd)
TS_CLIENT="$REPO/clients/typescript"

# --- 1. TypeScript client: the runner's `file:` dependency needs a compiled
#        dist/ (it imports the canonical sessionReducer/rootReducer/etc.).
#        Generated wire types (src/types/, gitignored) + dist/ are both built
#        from the canonical protocol sources. Bootstrap only what's missing.
if [ ! -f "$TS_CLIENT/dist/types/index.js" ]; then
  echo "building in-repo TypeScript client (clients/typescript)…"
  [ -d "$REPO/node_modules" ]            || (cd "$REPO" && npm install --no-audit --no-fund >/dev/null)
  [ -f "$TS_CLIENT/src/types/index.ts" ] || (cd "$REPO" && npm run generate:typescript >/dev/null)
  [ -d "$TS_CLIENT/node_modules" ]       || (cd "$TS_CLIENT" && npm install --no-audit --no-fund >/dev/null)
  (cd "$TS_CLIENT" && npm run build >/dev/null)
fi

# --- 2. Runner deps (resolves the file: TS client + ws).
echo "installing conformance runner deps…"
(cd "$DIR" && npm install --no-audit --no-fund >/dev/null)

# --- 3. Run the suite. It starts the B3 scenario-driven host per scenario,
#        connects over a real WebSocket, and rolls up GREEN/TOTAL.
echo "running host-conformance suite…"
echo ""
node "$DIR/conformance-suite.mjs" "$@"
