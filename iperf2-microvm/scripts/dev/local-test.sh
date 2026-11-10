#!/usr/bin/env bash
#
# Runs the app and a local stand-in for the gateway, then points a stock iperf2 client at it.
# No AWS account needed. Good for the first "does it work at all" check.
#
# Usage: scripts/dev/local-test.sh [iperf client options...]     (default: -t 5 -b 2M)
#   APP_BIN=build/image/iperf2-microvm   run the published native executable instead of building the dll
#
# Tip (WSL on a /mnt/c checkout): building in /mnt/c collides with Windows build output, so
# set WORK_DIR to a Linux path and the script copies the sources there first.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK_DIR="${WORK_DIR:-$ROOT}"
GATEWAY_PORT="${GATEWAY_PORT:-5001}"
OUT="${OUT:-/tmp/iperf2-local}"
mkdir -p "$OUT"

if [[ "$WORK_DIR" != "$ROOT" ]]; then
  mkdir -p "$WORK_DIR"
  tar -C "$ROOT" --exclude=bin --exclude=obj --exclude=.aws-sam --exclude=evidence -cf - src tools | tar -C "$WORK_DIR" -xf -
fi

if [[ -z "${APP_BIN:-}" ]]; then
  (cd "$WORK_DIR" && dotnet build src/Iperf2.MicroVm -c Release -o "$OUT/app" -v q --nologo 2>&1 | tail -2)
fi
(cd "$WORK_DIR" && dotnet build tools/LocalGateway -c Release -o "$OUT/gateway" -v q --nologo 2>&1 | tail -2)

cleanup() { kill "${APP:-}" "${GW:-}" 2>/dev/null || true; wait 2>/dev/null || true; }
trap cleanup EXIT

# Background processes get </dev/null so they never hold the terminal (or a wsl -e session) open.
if [[ -n "${APP_BIN:-}" ]]; then "$APP_BIN" < /dev/null > "$OUT/app.log" 2>&1 &
else dotnet "$OUT/app/iperf2-microvm.dll" < /dev/null > "$OUT/app.log" 2>&1 &
fi
APP=$!
for _ in $(seq 1 150); do
  curl -fs http://localhost:8080/ >/dev/null 2>&1 && break
  sleep 0.2
done
# Simulate the hooks Lambda calls. The Host header deliberately does not name the port, like the real
# platform: an app that routes on Host instead of the connection's port fails here, not after deploy.
READY=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Host: microvm' http://localhost:9000/aws/lambda-microvms/runtime/v1/ready)
[[ $READY == 200 ]] || { echo "/ready returned HTTP $READY (Lambda requires 200)" >&2; exit 1; }
echo "/ready -> 200"
curl -fs -X POST http://localhost:9000/aws/lambda-microvms/runtime/v1/run \
  -H 'Host: microvm' -H 'content-type: application/json' \
  -d '{"microvmId":"mvm-local","runHookPayload":"{\"Version\":1,\"TenantId\":\"local\",\"Region\":\"local\",\"ExtraData\":\"{\\\"FinGraceMs\\\":150}\"}"}' >/dev/null

dotnet "$OUT/gateway/LocalGateway.dll" "$GATEWAY_PORT" < /dev/null > "$OUT/gateway.log" 2>&1 &
GW=$!
sleep 1

if [[ -n "${LOCAL_CMD:-}" ]]; then
  # Run something else against the local listener, e.g.
  #   LOCAL_CMD="scripts/prove/01-deterministic.sh 127.0.0.1 5001" scripts/dev/local-test.sh
  echo "== $LOCAL_CMD"
  (cd "$ROOT" && bash -c "$LOCAL_CMD") || STATUS=$?
else
  ARGS=("$@"); [[ ${#ARGS[@]} -eq 0 ]] && ARGS=(-t 5 -b 2M)
  echo "== iperf client ${ARGS[*]}"
  iperf -u -c 127.0.0.1 -p "$GATEWAY_PORT" "${ARGS[@]}" | tee "$OUT/client.txt"
fi
sleep 1
echo "== app log (report events)"
grep '"Event":"report"' "$OUT/app.log" | jq -c 'del(.ReportHex)' || true
echo "full logs: $OUT/app.log $OUT/gateway.log"
exit "${STATUS:-0}"
