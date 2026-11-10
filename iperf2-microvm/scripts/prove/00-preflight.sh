#!/usr/bin/env bash
#
# Proof 0: the environment is ready and the MicroVM answers.
# Checks the local tools, reads the stack outputs, runs a 2-datagram warmup test, and confirms the
# MicroVM logged its /run hook (which carries its ID, tenant, and settings).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/iperf2-packets.sh"
source "$HERE/../lib/common.sh"

section "Preflight"
for tool in aws jq xxd iperf timeout dd awk curl; do
  if command -v "$tool" >/dev/null; then pass "tool available: $tool"; else fail "missing tool: $tool"; fi
done
IPERF_VERSION=$(iperf -v 2>&1 | head -1)
note "client: $IPERF_VERSION"
[[ $IPERF_VERSION == "iperf version 2."* ]] && pass "client is iperf2 (iperf3 is a different protocol)" \
  || fail "client is not iperf2: $IPERF_VERSION"

ACCOUNT=$(aws sts get-caller-identity --query Account --output text 2>/dev/null || true)
[[ -n "$ACCOUNT" ]] && pass "AWS credentials work (region $AWS_REGION)" || { fail "AWS credentials are not working (try: aws login)"; finish; exit 1; }

load_outputs
note "listener $DOMAIN:$PORT, tenant $TENANT_ID"
note "image $IMAGE_ARN"
[[ -n "$DOMAIN" && -n "$PORT" ]] && pass "stack outputs found" || fail "stack outputs missing (deploy first)"

if REPORT=$(warmup); then
  pass "warmup test answered: $(report_json "$REPORT")"
else
  fail "warmup test got no report; check $DELIVERY_LOG_GROUP"
fi

RUNNING=$(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
  --query "items[?state=='RUNNING'].microvmId" --output text | wc -w)
note "MicroVMs running from this image: $RUNNING"
[[ $RUNNING -ge 1 ]] && pass "a MicroVM is running" || fail "no RUNNING MicroVM listed"

# Ask the MicroVM itself (HTTPS endpoint + auth token): its /run event is logged once, when it starts.
backend_connect
[[ ${#_VM_TOKEN[@]} -ge 1 ]] && pass "connected to the MicroVM endpoint with a port-8080 auth token" \
  || fail "could not get an endpoint and token for a running MicroVM (needs lambda:CreateMicrovmAuthToken)"
RUN=$(backend_events run | jq -c --arg t "$TENANT_ID" 'select(.Tenant == $t)' | tail -1)
if [[ -n "$RUN" ]]; then
  pass "MicroVM $(jq -r .Microvm <<<"$RUN") ran its /run hook for tenant $TENANT_ID"
  note "run event: $RUN"
  save_evidence "00-run-event.json" "$RUN"
else
  fail "no run event for tenant $TENANT_ID in the MicroVM's event log"
fi

# Jitter and duration are measured from each packet's ReceivedAt, so it must have sub-second precision.
# (Loss, duplicates, and totals don't depend on it.) Look at what the warmup's batches carried.
SAMPLE=$(backend_events batch | jq -c 'select(.Messages > 0)' | tail -20)
MESSAGES=$(jq -s 'map(.Messages - .Dropped) | add // 0' <<<"$SAMPLE")
WHOLE=$(jq -s 'map(.WholeSecondTimestamps) | add // 0' <<<"$SAMPLE")
note "ReceivedAt as delivered: $(jq -r '.FirstReceivedAt' <<<"$SAMPLE" | tail -1) ($WHOLE of $MESSAGES recent timestamps are whole seconds)"
if (( MESSAGES > 0 && WHOLE * 10 < MESSAGES * 9 )); then
  pass "ReceivedAt has sub-second precision"
else
  fail "ReceivedAt has 1-second precision: jitter, duration, and arrival order within a second can't be measured (loss, duplicates, and totals are unaffected)"
fi
finish
