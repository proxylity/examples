#!/usr/bin/env bash
#
# Proof 5: the statistics really live in the MicroVM's memory, across many separate deliveries,
# and finished tests are cleaned up.
#
# A slow, long test (default 20 s at 100 Kbit/s, about 8 datagrams per second) arrives in hundreds of
# separate HTTPS batches. The MicroVM's batch log lines for that flow must all come from the same
# MicroVM, with the received count only ever going up, ending at the total in the client's report.
# Then, once the flow is reported and its 10 s replay cache expires, the MicroVM's periodic stats line
# must show zero active and zero completed flows.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/common.sh"
source "$HERE/../lib/iperf-output.sh"
load_outputs
DURATION=${DURATION:-20}

section "Long, slow test: state carried across many deliveries"
backend_mark
CLIENT_OUT="$EVIDENCE_DIR/05-client.txt"
iperf -u -c "$DOMAIN" -p "$PORT" -t "$DURATION" -b 100K > "$CLIENT_OUT" 2>&1 || true
STREAM=$(iperf_streams "$CLIENT_OUT" | head -1)
note "client: $STREAM"
TOTAL=$(jq .total <<<"$STREAM"); LOST=$(jq .lost <<<"$STREAM")
[[ $TOTAL != null ]] && pass "client got a server report" || { fail "no server report"; finish; exit 1; }

EVENT=$(backend_find report "select(.Replay == false and .Datagrams == $TOTAL)") || true
[[ -n "$EVENT" ]] || { fail "report event not found"; finish; exit 1; }
FLOW=$(jq -r .Flow <<<"$EVENT")
note "flow $FLOW"

BATCHES=$(backend_events batch |
  jq -c --arg f "$FLOW" 'select(any(.Flows[]; .Flow == $f)) | {Batch, Microvm, f: (.Flows[] | select(.Flow == $f))}')
save_evidence "05-batches.jsonl" "$BATCHES"
COUNT=$(grep -c . <<<"$BATCHES" || true)
note "$COUNT batches carried datagrams for this flow"
[[ $COUNT -ge 50 ]] && pass "the test spanned $COUNT separate deliveries" || fail "expected at least 50 deliveries, saw $COUNT"
expect "every delivery went to the same MicroVM" 1 "$(jq -r .Microvm <<<"$BATCHES" | sort -u | wc -l)"
MONOTONIC=$(jq -s 'sort_by(.Batch) | [.[].f.Received] as $r | all(range(1; $r | length); $r[.] >= $r[. - 1])' <<<"$BATCHES")
expect "received count only goes up, batch after batch" true "$MONOTONIC"
LAST=$(jq -s 'sort_by(.Batch) | last | .f | .Received + .Pending' <<<"$BATCHES")
# The FIN is counted when it arrives, so the data seen in batches is the total minus the FIN and the losses.
expect "datagrams seen in batches = total - FIN - lost" $(( TOTAL - 1 - LOST )) "$LAST"

section "Cleanup: finished flows leave memory"
note "waiting for the 10 s replay cache to expire and the next 30 s stats line..."
backend_mark # only stats lines written from now on count
STATS=$(backend_find stats 'select(.ActiveFlows == 0 and .CompletedFlows == 0)' 120) || true
if [[ -n "$STATS" ]]; then
  pass "the MicroVM reports 0 active and 0 completed flows"
  note "stats: $STATS"
  save_evidence "05-stats-event.json" "$STATS"
else
  fail "no stats line with an empty flow table within 2 minutes"
fi
finish
