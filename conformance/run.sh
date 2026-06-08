#!/usr/bin/env bash
# Live full-handshake conformance check: the IN-REPO .NET AhpClient vs a
# spec-faithful AHP host built on the IN-REPO TypeScript client's canonical
# `sessionReducer`, over a real WebSocket. Self-contained — every dependency
# resolves inside this repo (the .NET client via <ProjectReference>, the TS
# client via a `file:` dependency). No published package, no external checkout.
#
#   conformance/
#     host/      → host.mjs + package.json (file: dep on clients/typescript)
#     dotnet/    → FullHandshake (ProjectReference to clients/dotnet/src/*)
#     run.sh     → this script
#
# Expected tail:
#   FULL-HANDSHAKE LIVE PASS — initialize + snapshot + live action stream converge with the canonical reducer
set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$DIR/.." && pwd)
TS_CLIENT="$REPO/clients/typescript"
HOST_DIR="$DIR/host"

# --- 1. TypeScript client: the host's `file:` dependency needs a compiled dist/.
#        Generated wire types (src/types/, gitignored) + dist/ are both built
#        from the canonical protocol sources. Bootstrap only what's missing.
if [ ! -f "$TS_CLIENT/dist/types/index.js" ]; then
  echo "building in-repo TypeScript client (clients/typescript)…"
  [ -d "$REPO/node_modules" ]       || (cd "$REPO" && npm install --no-audit --no-fund >/dev/null)
  [ -f "$TS_CLIENT/src/types/index.ts" ] || (cd "$REPO" && npm run generate:typescript >/dev/null)
  [ -d "$TS_CLIENT/node_modules" ]  || (cd "$TS_CLIENT" && npm install --no-audit --no-fund >/dev/null)
  (cd "$TS_CLIENT" && npm run build >/dev/null)
fi

# --- 2. Conformance host: install deps (resolves the file: TS client + ws).
echo "installing conformance host deps…"
(cd "$HOST_DIR" && npm install --no-audit --no-fund >/dev/null)

# --- 3. .NET client + FullHandshake (Release/net8.0). Building FullHandshake
#        builds the referenced client libs through its <ProjectReference> chain.
echo "building in-repo .NET client + FullHandshake (Release/net8.0)…"
dotnet build "$DIR/dotnet/FullHandshake" -c Release -f net8.0 >/dev/null

# --- 4. Start the host, capture its ws:// URL.
rm -f "$HOST_DIR/final.json" "$HOST_DIR/host.log"
node "$HOST_DIR/host.mjs" > "$HOST_DIR/host.log" 2>&1 &
HPID=$!; trap 'kill $HPID 2>/dev/null' EXIT
for i in $(seq 1 50); do
  URL=$(grep -oE 'ws://127.0.0.1:[0-9]+' "$HOST_DIR/host.log" | head -1 || true)
  [ -n "${URL:-}" ] && break
  sleep 0.2
done
[ -z "${URL:-}" ] && { echo "host failed to start:"; cat "$HOST_DIR/host.log"; exit 1; }
echo "compliant host: $URL"

# --- 5. Run the real .NET client against it; assert the PASS line.
OUT=$(dotnet run --project "$DIR/dotnet/FullHandshake" -c Release -f net8.0 --no-build -- "$URL" "$HOST_DIR/final.json")
echo "$OUT"
echo "$OUT" | grep -q "FULL-HANDSHAKE LIVE PASS" || { echo "CONFORMANCE FAILED — expected PASS line not found"; exit 1; }
