#!/usr/bin/env bash
# AHP Go conformance runner — build-phase B5.
#
# Usage:
#   ./run.sh                    # full 233-scenario corpus
#   ./run.sh --brief            # brief tranche (23 round-trips + 30 reducers + 46 negatives)
#   ./run.sh --verbose          # per-assertion detail
#   ./run.sh --concurrency 8    # more parallel host processes
#
# Requires: go 1.22+, node (for the scenario host)

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SCENARIOS="$REPO_ROOT/types/test-cases/scenarios"

cd "$SCRIPT_DIR"
exec go run . "$SCENARIOS" --all-reducers "$@"
