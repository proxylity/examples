#!/usr/bin/env bash
#
# Proof 1: hand-crafted traffic with a known correct answer.
#
# Sends iperf2 datagrams built in bash, with deliberate faults, and checks the server report
# field by field against answers worked out in advance. The input is fixed, so the answers are
# exact. Datagrams are 100 bytes and sent 10 ms apart; the FIN is retried every 20 ms until the
# report comes back, as the real client does.
#
# Scenario A: loss, reordering, and a late arrival
#   seq 1..50 except: 10 never sent; 31 sent before 30; 20 held back and sent after 45
#   FIN -51
#   Expected: datagrams 51 (50 data + FIN), lost 1, out-of-order 2, bytes 50 x 100 = 5000
#   A stock iperf2 server gives the same answer (checked with ORACLE=1).
#
# Scenario B: a duplicate alongside a loss
#   seq 1..20 except: 10 never sent; 5 sent twice
#   FIN -21
#   Expected: datagrams 21, lost 1, out-of-order 0, bytes 20 x 100 = 2000, 1 duplicate logged
#   A stock iperf2 server does not detect duplicates: it counts the second 5 as received data
#   (bytes 2100) and as out-of-order, and the extra "received" datagram cancels the real loss
#   (lost 0). This backend tracks every sequence number in a bitmap, so it gets this right.
#   With ORACLE=1 the script shows the difference rather than requiring a match.
#
# Usage: scripts/prove/01-deterministic.sh [HOST PORT]
#   With no arguments, targets the deployed listener (from the stack outputs).
#   ORACLE=1 also sends the same traffic to a local stock "iperf -s -u" and compares.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/iperf2-packets.sh"
source "$HERE/../lib/common.sh"

LENGTH=100

# send_scenario A|B HOST PORT -> prints the report hex
send_scenario() {
  local scenario=$1 seq last
  iperf2_open "$2" "$3"
  if [[ $scenario == A ]]; then
    last=50
    for seq in $(seq 1 $last); do
      case $seq in
        10|20|31) ;;                                                       # lost / held back / sent early
        30) iperf2_send 31 $LENGTH; sleep 0.01; iperf2_send 30 $LENGTH ;;   # swapped
        45) iperf2_send 45 $LENGTH; sleep 0.01; iperf2_send 20 $LENGTH ;;   # late arrival
        *)  iperf2_send "$seq" $LENGTH ;;
      esac
      sleep 0.01
    done
  else
    last=20
    for seq in $(seq 1 $last); do
      case $seq in
        10) ;;                                                             # lost
        5)  iperf2_send 5 $LENGTH; sleep 0.01; iperf2_send 5 $LENGTH ;;     # duplicate
        *)  iperf2_send "$seq" $LENGTH ;;
      esac
      sleep 0.01
    done
  fi
  sleep 0.2 # let the last data settle into the gateway's batches before ending the test
  local report
  report=$(iperf2_finish $(( last + 1 )) $LENGTH) || { iperf2_close; return 1; }
  iperf2_close
  echo "$report"
}

check_a() { # NAME JSON
  expect "$1: report is 128 bytes (64-bit form)" 128 "$(jq .length <<<"$2")"
  expect "$1: datagrams = 51 (50 data + FIN)" 51 "$(jq .datagrams <<<"$2")"
  expect "$1: lost = 1 (seq 10)" 1 "$(jq .lost <<<"$2")"
  expect "$1: out-of-order = 2 (30 after 31, late 20)" 2 "$(jq .outOfOrder <<<"$2")"
  expect "$1: bytes = 5000" 5000 "$(jq .bytes <<<"$2")"
}

check_b() { # NAME JSON
  expect "$1: datagrams = 21 (20 data + FIN)" 21 "$(jq .datagrams <<<"$2")"
  expect "$1: lost = 1 (seq 10; the duplicate does not hide it)" 1 "$(jq .lost <<<"$2")"
  expect "$1: out-of-order = 0" 0 "$(jq .outOfOrder <<<"$2")"
  expect "$1: bytes = 2000 (duplicate not counted)" 2000 "$(jq .bytes <<<"$2")"
}

# backend_check SCENARIO HEX EXPECTED_DUPLICATES
backend_check() {
  local event
  event=$(backend_find report "select(.ReportHex == \"$(tr a-f A-F <<<"$2")\")") || true
  if [[ -z "$event" ]]; then
    fail "$1 backend: report event with the same bytes not found in the MicroVM's event log"
    return
  fi
  pass "$1 backend: the MicroVM logged a report with exactly the bytes the client received"
  note "$1 backend: $(jq -c 'del(.ReportHex)' <<<"$event")"
  expect "$1 backend: duplicates = $3" "$3" "$(jq .Duplicates <<<"$event")"
  save_evidence "01-$1-report-event.json" "$event"
}

if [[ $# -ge 2 ]]; then
  HOST=$1; PORT=$2; REMOTE=0
else
  load_outputs; HOST=$DOMAIN; REMOTE=1
fi

declare -A JSON
for scenario in A B; do
  section "Scenario $scenario: deterministic traffic to $HOST:$PORT"
  [[ $REMOTE == 1 ]] && backend_mark
  if ! HEX=$(send_scenario $scenario "$HOST" "$PORT"); then
    fail "scenario $scenario: no server report received"
    continue
  fi
  JSON[$scenario]=$(report_json "$HEX")
  note "client received: ${JSON[$scenario]}"
  save_evidence "01-$scenario-client-report.json" "${JSON[$scenario]}"
  "check_$(tr A-Z a-z <<<"$scenario")" "listener" "${JSON[$scenario]}"
  if [[ $REMOTE == 1 ]]; then
    backend_check "$scenario" "$HEX" "$([[ $scenario == B ]] && echo 1 || echo 0)"
  fi
done

if [[ "${ORACLE:-0}" == 1 ]]; then
  ORACLE_PORT=${ORACLE_PORT:-5201}
  iperf -s -u -p "$ORACLE_PORT" < /dev/null > /tmp/iperf2-oracle-server.txt 2>&1 &
  ORACLE_PID=$!
  stop_oracle() { kill "$ORACLE_PID" 2>/dev/null || true; wait "$ORACLE_PID" 2>/dev/null || true; }
  trap stop_oracle EXIT
  sleep 0.5

  section "Oracle, scenario A: the same traffic to a stock iperf2 server ($(iperf -v 2>&1 | head -1 | cut -d' ' -f1-3))"
  if OHEX=$(send_scenario A 127.0.0.1 "$ORACLE_PORT"); then
    OJSON=$(report_json "$OHEX")
    note "stock server: $OJSON"
    save_evidence "01-A-oracle-report.json" "$OJSON"
    for field in length datagrams lost outOfOrder bytes; do
      expect "listener matches stock server: $field" "$(jq ".$field" <<<"$OJSON")" "$(jq ".$field" <<<"${JSON[A]:-null}")"
    done
  else
    fail "stock server did not answer"
  fi

  section "Oracle, scenario B: where stock iperf2 and this backend differ (duplicates)"
  if OHEX=$(send_scenario B 127.0.0.1 "$ORACLE_PORT"); then
    OJSON=$(report_json "$OHEX")
    note "stock server: $OJSON"
    note "listener:     ${JSON[B]:-none}"
    note "stock iperf2 counts the duplicate as received data, which hides the loss of seq 10"
    save_evidence "01-B-oracle-report.json" "$OJSON"
    expect "stock server: datagrams agree" "$(jq .datagrams <<<"$OJSON")" "$(jq .datagrams <<<"${JSON[B]:-null}")"
  else
    fail "stock server did not answer"
  fi
  stop_oracle
fi

finish
