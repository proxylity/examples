#!/usr/bin/env bash
#
# Proof 4: many simultaneous flows in one MicroVM, with no mixing between them.
#
# Runs, at the same time: a 4-stream client (-P 4, 1200-byte datagrams) and a 1-stream client
# (500-byte datagrams). All five flows share one MicroVM and often share batches. Every stream must
# get its own server report, and every backend report must be made only of datagrams of its own size:
# Bytes = (Datagrams - Lost) x length. A single stray datagram from another flow would break that.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/common.sh"
source "$HERE/../lib/iperf-output.sh"
load_outputs
DURATION=${DURATION:-8}

section "Parallel: -P 4 -l 1200 and -l 500 at the same time"
backend_mark
iperf -u -c "$DOMAIN" -p "$PORT" -t "$DURATION" -b 400K -l 1200 -P 4 > "$EVIDENCE_DIR/04-client-p4.txt" 2>&1 &
P4=$!
iperf -u -c "$DOMAIN" -p "$PORT" -t "$DURATION" -b 300K -l 500 > "$EVIDENCE_DIR/04-client-l500.txt" 2>&1 &
L500=$!
wait $P4 $L500 || true

check_client() { # FILE EXPECTED_STREAMS
  local streams count reported
  streams=$(iperf_streams "$1")
  count=$(grep -c . <<<"$streams" || true)
  reported=$(jq -s '[.[] | select(.total != null)] | length' <<<"$streams")
  note "$(basename "$1"): $(tr '\n' ' ' <<<"$streams")"
  expect "$(basename "$1"): every stream got a server report" "$2/$2" "$reported/$count"
}
check_client "$EVIDENCE_DIR/04-client-p4.txt" 4
check_client "$EVIDENCE_DIR/04-client-l500.txt" 1

section "Backend evidence: one report per flow, each made only of its own datagrams"
REPORTS=$(backend_events report | jq -c 'select(.Replay == false)')
save_evidence "04-report-events.jsonl" "$REPORTS"
FLOWS=$(jq -r .Flow <<<"$REPORTS" | sort -u | wc -l)
expect "five distinct flows reported" 5 "$FLOWS"
MICROVMS=$(jq -r .Microvm <<<"$REPORTS" | sort -u | wc -l)
expect "all flows were handled by one MicroVM" 1 "$MICROVMS"
while read -r report; do
  [[ -z "$report" ]] && continue
  flow=$(jq -r .Flow <<<"$report"); bytes=$(jq .Bytes <<<"$report")
  received=$(( $(jq .Datagrams <<<"$report") - $(jq .Lost <<<"$report") ))
  if (( bytes == received * 1200 )); then pass "$flow: $received datagrams x 1200 bytes, no mixing"
  elif (( bytes == received * 500 )); then pass "$flow: $received datagrams x 500 bytes, no mixing"
  else fail "$flow: $bytes bytes is not $received x 1200 or x 500 (mixed or miscounted)"
  fi
done <<<"$REPORTS"
finish
