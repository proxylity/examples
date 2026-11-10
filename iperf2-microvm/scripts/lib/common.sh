#!/usr/bin/env bash
#
# Shared helpers for the deploy and proof scripts: stack outputs, CloudWatch log queries,
# and a tiny PASS/FAIL recorder that also writes evidence files.

STACK_NAME="${STACK_NAME:-iperf2-microvm}"
export AWS_REGION="${AWS_REGION:-us-west-2}"
export AWS_DEFAULT_REGION="$AWS_REGION"
EXAMPLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EVIDENCE_DIR="${EVIDENCE_DIR:-$EXAMPLE_ROOT/evidence/latest}"
mkdir -p "$EVIDENCE_DIR"

PASSES=0
FAILS=0

now_ms() { date +%s%3N; }

section() { printf '\n== %s\n' "$*"; printf '\n### %s\n\n' "$*" >> "$EVIDENCE_DIR/summary.md"; }
note()    { printf '   %s\n' "$*"; printf -- '- %s\n' "$*" >> "$EVIDENCE_DIR/summary.md"; }
pass()    { PASSES=$((PASSES + 1)); printf ' PASS %s\n' "$*"; printf -- '- PASS %s\n' "$*" >> "$EVIDENCE_DIR/summary.md"; }
fail()    { FAILS=$((FAILS + 1)); printf ' FAIL %s\n' "$*"; printf -- '- **FAIL** %s\n' "$*" >> "$EVIDENCE_DIR/summary.md"; }

# expect DESCRIPTION EXPECTED ACTUAL
expect() {
  if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1 (expected $2, got $3)"; fi
}

# within DESCRIPTION EXPECTED ACTUAL TOLERANCE   (numeric, absolute tolerance)
within() {
  if awk -v e="$2" -v a="$3" -v t="$4" 'BEGIN { d = a - e; if (d < 0) d = -d; exit !(d <= t) }'; then
    pass "$1 ($3, expected $2 +/- $4)"
  else
    fail "$1 (got $3, expected $2 +/- $4)"
  fi
}

save_evidence() { printf '%s\n' "$2" > "$EVIDENCE_DIR/$1"; }

finish() {
  printf '\n%d passed, %d failed\n' "$PASSES" "$FAILS"
  printf '\n%d passed, %d failed\n' "$PASSES" "$FAILS" >> "$EVIDENCE_DIR/summary.md"
  [[ $FAILS -eq 0 ]]
}

# Reads the stack outputs into DOMAIN, PORT, IMAGE_ARN, LOG_GROUP, TENANT_ID.
load_outputs() {
  local outputs
  outputs=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" \
    --query 'Stacks[0].Outputs' --output json)
  out() { jq -r --arg k "$1" '.[] | select(.OutputKey == $k) | .OutputValue' <<<"$outputs"; }
  DOMAIN=$(out Domain)
  PORT=$(out Port)
  IMAGE_ARN=$(out MicrovmImageArn)
  LOG_GROUP=$(out MicrovmLogGroup)
  DELIVERY_LOG_GROUP=$(out DeliveryLogGroup)
  TENANT_ID=$(out TenantId)
  [[ -n "$DOMAIN" && -n "$IMAGE_ARN" ]] || { echo "Stack $STACK_NAME has no outputs; deploy it first (scripts/deploy.sh)." >&2; return 1; }
  export DOMAIN PORT IMAGE_ARN LOG_GROUP DELIVERY_LOG_GROUP TENANT_ID
}

# ---- Backend evidence, read from the MicroVM itself ----
#
# The app keeps its recent events (run, batch, fin, report, stats) in memory and serves them at
# GET /events on port 8080. We reach it the way Proxylity does: the MicroVM's HTTPS endpoint plus a
# short-lived auth token for port 8080. The same events also reach CloudWatch Logs (stdout, one stream per
# MicroVM), but reading them from the MicroVM is immediate and needs no log-delivery delay or polling.
#
# Typical use:
#   backend_mark                                  # remember where each MicroVM's event log is now
#   ...run a test...
#   backend_events report | jq 'select(...)'      # events since the mark, one JSON object per line
#   backend_find report 'select(.Datagrams == 51)' # first match, retrying briefly

declare -gA _VM_URL _VM_TOKEN _VM_MARK

# Refreshes the endpoint and token for every RUNNING MicroVM started from the image.
backend_connect() {
  local id endpoint token
  for id in $(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
      --query "items[?state=='RUNNING'].microvmId" --output text); do
    [[ -n "${_VM_TOKEN[$id]:-}" ]] && continue
    endpoint=$(aws lambda-microvms get-microvm --microvm-identifier "$id" --query endpoint --output text)
    [[ $endpoint == http* ]] || endpoint="https://$endpoint"
    token=$(aws lambda-microvms create-microvm-auth-token --microvm-identifier "$id" \
      --expiration-in-minutes 60 --allowed-ports '[{"port":8080}]' --output json | jq -r '.authToken["X-aws-proxy-auth"] // empty')
    [[ -n "$token" ]] || continue
    _VM_URL[$id]=${endpoint%/}
    _VM_TOKEN[$id]=$token
  done
}

# backend_get MICROVM_ID PATH -> response body
backend_get() {
  curl -s --max-time 20 -H "X-aws-proxy-auth: ${_VM_TOKEN[$1]}" "${_VM_URL[$1]}$2"
}

# Records each MicroVM's current event cursor; later reads return only newer events.
backend_mark() {
  local id
  backend_connect
  for id in "${!_VM_TOKEN[@]}"; do
    _VM_MARK[$id]=$(backend_get "$id" "/events?from=-1" | jq -r '.Next // 1')
  done
}

# backend_events [TYPE] -> events since the last backend_mark (all events for MicroVMs started since), one per line
backend_events() {
  local id
  backend_connect
  for id in "${!_VM_TOKEN[@]}"; do
    backend_get "$id" "/events?from=${_VM_MARK[$id]:-0}${1:+&type=$1}" | jq -c '.Events[]?'
  done
}

# backend_find TYPE JQ_FILTER [TIMEOUT_S] -> first matching event
backend_find() {
  local deadline=$(( $(date +%s) + ${3:-15} )) found
  while (( $(date +%s) < deadline )); do
    found=$(backend_events "$1" | jq -c "$2" | head -1)
    [[ -n "$found" ]] && { echo "$found"; return 0; }
    sleep 2
  done
  return 1
}

# Runs a tiny iperf2 test (2 datagrams + FIN) and waits for the report, so the MicroVM is known to be
# running and answering before a measured test. Starting a MicroVM takes a moment, and the first
# packets for a new tenant can arrive before it is ready. Needs scripts/lib/iperf2-packets.sh.
warmup() {
  local attempt report
  for attempt in 1 2 3 4 5; do
    iperf2_open "$DOMAIN" "$PORT"
    iperf2_send 1 100; sleep 0.05; iperf2_send 2 100; sleep 0.1
    if report=$(iperf2_finish 3 100 100); then
      iperf2_close
      echo "$report"
      return 0
    fi
    iperf2_close
    sleep 3
  done
  return 1
}
