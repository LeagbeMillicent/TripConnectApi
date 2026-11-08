#!/usr/bin/env bash
# Regenerates the API contract from the running code, then the Postman collection from it.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SPEC="$ROOT/postman/specs/riconride-openapi.json"
PORT=5099

(cd "$ROOT" && docker compose up -d --wait postgres >/dev/null)

ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$PORT" \
  dotnet run --no-launch-profile --project "$ROOT/backend/src/RiconRide.Api" >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT

for _ in $(seq 1 90); do
  curl -sf "http://localhost:$PORT/health" >/dev/null && break
  kill -0 $PID 2>/dev/null || { echo "API exited during startup; run it with dotnet run to see why." >&2; exit 1; }
  sleep 1
done
# Pretty-print so diffs of the contract are reviewable.
curl -sf "http://localhost:$PORT/openapi/v1.json" | python3 -m json.tool --indent 2 > "$SPEC"
echo "Wrote $SPEC"

cd "$ROOT" && postman spec generate collection "$SPEC" -n "RiconRide API" --folder-strategy Tags --force
