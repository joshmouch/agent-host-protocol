#!/usr/bin/env bash
#
# .NET test-parity gate.
#
# Two complementary checks against the .NET test suite:
#   1. Count floor (clients/dotnet/tests/MIN_TEST_COUNT) — a ratchet so the number
#      of discrete [Fact]/[Theory] methods never regresses (catches deletions).
#   2. Parity manifest (clients/dotnet/tests/parity-manifest.txt) — the expected
#      parity test methods (executable form of the master matrix). Any manifest
#      entry whose method name is absent from the test sources is "missing".
#
# Modes:
#   check-test-parity.sh             COMPLETE  - fail if ANY manifest test is
#                                     missing; enumerate the missing ones. Used as
#                                     the BLOCKING CI gate (.github/workflows/ci.yml).
#   check-test-parity.sh --ratchet   RATCHET   - fail only if the method count
#                                     dropped below the floor; never blocks
#                                     in-progress work. Used by the local pre-push hook.
#   check-test-parity.sh --list      report present/missing, never fail.
#   check-test-parity.sh --bump      raise the floor to the current method count.
#
# Plan: OpenAgency docs/plans/proposed/2026-06-04-0137-ahp-dotnet-client-test-parity
set -euo pipefail

PLAN="docs/plans/proposed/2026-06-04-0137-ahp-dotnet-client-test-parity (AHP .NET client full-parity)"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
TEST_DIR="$ROOT/clients/dotnet/tests/AgentHostProtocol.Tests"
FLOOR_FILE="$ROOT/clients/dotnet/tests/MIN_TEST_COUNT"
MANIFEST="$ROOT/clients/dotnet/tests/parity-manifest.txt"

[ -d "$TEST_DIR" ]   || { echo "check-test-parity: missing test dir $TEST_DIR" >&2; exit 1; }
[ -f "$FLOOR_FILE" ] || { echo "check-test-parity: missing floor file $FLOOR_FILE" >&2; exit 1; }
[ -f "$MANIFEST" ]   || { echo "check-test-parity: missing manifest $MANIFEST" >&2; exit 1; }

# Restrict to source .cs and skip build output (bin/obj) — recursing compiled
# binaries makes grep read every byte for non-matching patterns (minutes, not ms).
GREP_SCOPE=(--include='*.cs' --exclude-dir=bin --exclude-dir=obj)

method_count() { { grep -rhE "${GREP_SCOPE[@]}" '^[[:space:]]*\[(Fact|Theory)\]' "$TEST_DIR" 2>/dev/null || true; } | wc -l | tr -d ' '; }

# Populate the missing/present arrays from the manifest.
missing=()        # "phase|suite|method|label"
present_manifest=0
total_manifest=0
while IFS= read -r raw; do
  line="${raw%%#*}"                                  # strip comments
  [ -z "${line// }" ] && continue                    # skip blank
  IFS='|' read -r method suite phase label <<EOF
$line
EOF
  method="$(echo "$method" | tr -d '[:space:]')"
  suite="$(echo "$suite" | sed -E 's/^ *| *$//g')"
  phase="$(echo "$phase" | tr -d '[:space:]')"
  label="$(echo "$label" | sed -E 's/^ *| *$//g')"
  [ -z "$method" ] && continue
  total_manifest=$((total_manifest + 1))
  if grep -rwqF "${GREP_SCOPE[@]}" "$method" "$TEST_DIR" 2>/dev/null; then
    present_manifest=$((present_manifest + 1))
  else
    missing+=("$phase|$suite|$method|$label")
  fi
done < "$MANIFEST"

COUNT="$(method_count)"
FLOOR="$(tr -cd '0-9' < "$FLOOR_FILE")"; : "${FLOOR:=0}"
missing_count=${#missing[@]}

enumerate_missing() {
  local target_phase="$1" ph su me la shown=0
  for phase in 1 2; do
    [ -n "$target_phase" ] && [ "$target_phase" != "$phase" ] && continue
    local header_done=0
    for entry in ${missing[@]+"${missing[@]}"}; do
      IFS='|' read -r ph su me la <<EOF
$entry
EOF
      [ "$ph" = "$phase" ] || continue
      if [ "$header_done" = 0 ]; then
        echo "  Phase $phase:" >&2; header_done=1
      fi
      printf '    [ ] %-55s %s\n' "$su.$me" "($la)" >&2
      shown=$((shown + 1))
    done
  done
  if [ "$shown" = 0 ]; then echo "    (none)" >&2; fi
  return 0
}

case "${1:-}" in
  --bump)
    if [ "$COUNT" -gt "$FLOOR" ]; then
      printf '%s\n' "$COUNT" > "$FLOOR_FILE"
      echo "check-test-parity: floor raised $FLOOR -> $COUNT"
    else
      echo "check-test-parity: floor unchanged ($FLOOR); current $COUNT is not higher"
    fi
    exit 0
    ;;
  --list)
    echo "check-test-parity: $COUNT test methods (floor $FLOOR); parity $present_manifest/$total_manifest present, $missing_count missing"
    [ "$missing_count" -gt 0 ] && { echo "missing parity tests:" >&2; enumerate_missing ""; }
    exit 0
    ;;
  --ratchet)
    if [ "$COUNT" -lt "$FLOOR" ]; then
      {
        echo "check-test-parity: FAIL - .NET test methods regressed: $COUNT < floor $FLOOR"
        echo "  A [Fact]/[Theory] was removed. Restore it, or - if intentional - lower"
        echo "  clients/dotnet/tests/MIN_TEST_COUNT in the same commit and explain why."
        echo "  Plan: $PLAN"
        echo "  Parity tests still missing ($missing_count of $total_manifest):"
        enumerate_missing ""
      } >&2
      exit 1
    fi
    echo "check-test-parity: ok - $COUNT methods >= floor $FLOOR; parity $present_manifest/$total_manifest present, $missing_count remaining (see plan; run --list to enumerate)"
    exit 0
    ;;
  "" )
    if [ "$missing_count" -gt 0 ]; then
      {
        echo "check-test-parity: FAIL - .NET client is not at test parity: $missing_count of $total_manifest expected tests are missing ($present_manifest present)."
        echo "  Plan: $PLAN"
        echo "  Add the following test methods (named per the parity manifest,"
        echo "  clients/dotnet/tests/parity-manifest.txt):"
        enumerate_missing ""
      } >&2
      exit 1
    fi
    echo "check-test-parity: ok - all $total_manifest parity tests present ($COUNT total methods, floor $FLOOR)."
    exit 0
    ;;
  * )
    echo "check-test-parity: unknown argument '$1' (use --ratchet | --list | --bump | no-arg)" >&2
    exit 2
    ;;
esac
