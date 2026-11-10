#!/usr/bin/env bash
# What the running MicroVM saw recently: per-flow data/FIN/report summary, and a sample of raw ReceivedAt values.
# Usage: scripts/dev/recent.sh [LAST_N_EVENTS]   (default 3000)
source "$(dirname "$0")/../lib/common.sh"
load_outputs
backend_connect
N=${1:-3000}
for id in "${!_VM_TOKEN[@]}"; do
  NEXT=$(backend_get "$id" "/events?from=-1" | jq -r .Next)
  FROM=$(( NEXT > N ? NEXT - N : 0 ))
  EVENTS=$(backend_get "$id" "/events?from=$FROM" | jq -c '.Events[]')
  echo "== $id  (events $FROM..$(( NEXT - 1 )))"
  echo "-- flows: data datagrams received (last batch counters), FIN actions, reports"
  jq -rs '
    (map(select(.Event == "batch")) | map(.Flows[]) | group_by(.Flow) | map({key: .[0].Flow, value: (last | "received=\(.Received) pending=\(.Pending) highest=\(.Highest)")}) | from_entries) as $data
    | (map(select(.Event == "fin")) | group_by(.Flow) | map({key: .[0].Flow, value: (group_by(.Action) | map("\(.[0].Action)=\(length)") | join(","))}) | from_entries) as $fins
    | (map(select(.Event == "report" and .Replay == false)) | map({key: .Flow, value: "report: \(.Datagrams) total, \(.Lost) lost"}) | from_entries) as $reports
    | ($data + $fins + $reports | keys)[] as $f
    | "\($f)  \($data[$f] // "no data")  fins[\($fins[$f] // "none")]  \($reports[$f] // "no report")"' <<<"$EVENTS"
  echo "-- latest batches"
  jq -c 'select(.Event == "batch" and .Messages > 0) | {Batch, Messages, Dropped, FirstReceivedAt, WholeSecondTimestamps, ProcessedAt}' <<<"$EVENTS" | tail -4
done
