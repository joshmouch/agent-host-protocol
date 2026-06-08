#!/usr/bin/env bash
# AHP Kotlin conformance runner.
#
# Drives the REAL Kotlin client (reducer + types + KSerializer) against the
# scenario-driven host over a REAL WebSocket, for every scenario in the
# selected tranche. No mocks: real subprocess, real ws, real reducers, real assertions.
#
# Prerequisites:
#   1. Build the Kotlin client JAR (if not already built):
#        JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home \
#          ../../../clients/kotlin/gradlew --project-dir ../../../clients/kotlin jar -x test
#   2. Node.js must be on PATH (for the scenario host).
#
# Usage:
#   ./run.sh                    # brief tranche: 23 round-trips + 30 reducers + 46 negatives = 99
#   ./run.sh --full             # full tranche: all 233 scenarios
#   JAVA_HOME=... ./run.sh      # override JDK path

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TRANCHE="brief"
for arg in "$@"; do
  case "$arg" in
    --full) TRANCHE="full" ;;
  esac
done

# Use Homebrew OpenJDK 17 as default if JAVA_HOME not set.
if [[ -z "${JAVA_HOME:-}" ]]; then
  if [[ -d /opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home ]]; then
    export JAVA_HOME=/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
  elif [[ -d /usr/local/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home ]]; then
    export JAVA_HOME=/usr/local/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home
  fi
fi

echo "[AHP] Kotlin conformance runner — tranche: $TRANCHE"
echo "[AHP] JAVA_HOME: ${JAVA_HOME:-<not set>}"
echo ""

exec "$SCRIPT_DIR/gradlew" \
  --project-dir "$SCRIPT_DIR" \
  test \
  --rerun \
  "-Dahp.tranche=$TRANCHE"
