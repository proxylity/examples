#!/usr/bin/env bash
#
# Proof 2: an unmodified iperf2 client runs a normal UDP test and gets a normal server report,
# and that report is exactly what the backend calculated and logged.
#
#   - the client prints a "Server Report" (it only does that when it gets a valid 128-byte report)
#   - Total = Sent - 1, the same relationship a stock iperf2 server gives (the client counts one more
#     datagram than the first FIN's sequence number)
#   - the MicroVM logged a report with the same total, loss, and jitter
#
# Usage: scripts/prove/02-stock-client.sh [iperf client options]    (default: -t 10 -b 1M)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/common.sh"
source "$HERE/../lib/iperf-output.sh"
load_outputs

ARGS=("$@"); [[ ${#ARGS[@]} -eq 0 ]] && ARGS=(-t 10 -b 1M)
section "Stock client: iperf -u -c $DOMAIN -p $PORT ${ARGS[*]}"
backend_mark
CLIENT_OUT="$EVIDENCE_DIR/02-client.txt"
iperf -u -c "$DOMAIN" -p "$PORT" "${ARGS[@]}" > "$CLIENT_OUT" 2>&1 || true
sed 's/^/   | /' "$CLIENT_OUT"

STREAM=$(iperf_streams "$CLIENT_OUT" | head -1)
note "client stream: ${STREAM:-none}"
if [[ -z "$STREAM" || $(jq .total <<<"$STREAM") == null ]]; then
  fail "the client did not print a server report"
  finish; exit 1
fi
pass "the client printed a server report"
SENT=$(jq .sent <<<"$STREAM"); TOTAL=$(jq .total <<<"$STREAM"); LOST=$(jq .lost <<<"$STREAM")
expect "total = sent - 1 (same as a stock iperf2 server)" $(( SENT - 1 )) "$TOTAL"
LOSS_PCT=$(awk -v l="$LOST" -v t="$TOTAL" 'BEGIN { printf "%.2f", 100 * l / t }')
note "loss over the internet path: $LOST/$TOTAL ($LOSS_PCT%)"
awk -v p="$LOSS_PCT" 'BEGIN { exit !(p < 5) }' && pass "loss below 5% on an unimpaired path" \
  || fail "loss of $LOSS_PCT% is high for an unimpaired path; check your network"

section "Backend evidence (the MicroVM's event log)"
EVENT=$(backend_find report "select(.Replay == false and .Datagrams == $TOTAL and .Lost == $LOST)") || true
if [[ -z "$EVENT" ]]; then
  fail "no report event with Datagrams=$TOTAL and Lost=$LOST in the MicroVM's event log"
else
  note "backend: $(jq -c 'del(.ReportHex)' <<<"$EVENT")"
  pass "the MicroVM logged the same total ($TOTAL) and loss ($LOST)"
  # The client prints jitter in ms with 3 decimals; the backend logs microseconds.
  within "jitter matches (ms)" "$(jq .jitterMs <<<"$STREAM")" \
    "$(jq '.JitterUs / 1000' <<<"$EVENT")" 0.001
  save_evidence "02-report-event.json" "$EVENT"
  FLOW=$(jq -r .Flow <<<"$EVENT")
  BATCHES=$(backend_events batch | jq -c --arg f "$FLOW" 'select(any(.Flows[]; .Flow == $f))' | wc -l)
  note "this test's datagrams arrived in $BATCHES batches (separate HTTPS deliveries to the MicroVM)"
  [[ $BATCHES -gt 1 ]] && pass "the test spanned many deliveries, and state carried across them" \
    || fail "expected the test to span more than one batch"
  echo "$FLOW" > "$EVIDENCE_DIR/02-flow.txt"
fi
finish
