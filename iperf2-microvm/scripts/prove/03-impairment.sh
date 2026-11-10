#!/usr/bin/env bash
#
# Proof 3: inject known network faults on this machine and check the backend measures them.
#
# Uses Linux traffic control (tc netem) on the interface that carries traffic to the listener, so
# datagrams leaving this machine are randomly dropped, duplicated, and reordered at known rates. The
# backend should report loss near the injected rate, log the duplicates (and not count them), and
# see reordering. Needs sudo. In WSL 2, netem is available in recent kernels; if it isn't, the
# script falls back to iptables random drop (loss only).
#
# Usage: sudo -v && scripts/prove/03-impairment.sh
#   LOSS=5 DUP=2 REORDER=25 (percent), RATE=2M, DURATION=10
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/../lib/common.sh"
source "$HERE/../lib/iperf-output.sh"
load_outputs

LOSS=${LOSS:-5} DUP=${DUP:-2} REORDER=${REORDER:-25} RATE=${RATE:-2M} DURATION=${DURATION:-10}
TARGET_IP=$(getent ahostsv4 "$DOMAIN" | awk 'NR == 1 { print $1 }')
DEV=$(ip route get "$TARGET_IP" | awk '{ for (i = 1; i < NF; i++) if ($i == "dev") print $(i + 1) }' | head -1)
section "Impairment on $DEV: loss $LOSS%, duplicate $DUP%, reorder $REORDER%"

sudo -v || { fail "sudo is required for tc/iptables"; finish; exit 1; }
MODE=""
cleanup() {
  [[ $MODE == netem ]] && sudo tc qdisc del dev "$DEV" root 2>/dev/null || true
  [[ $MODE == iptables ]] && sudo iptables -D OUTPUT -p udp -d "$TARGET_IP" --dport "$PORT" \
    -m statistic --mode random --probability "$(awk -v l="$LOSS" 'BEGIN { print l / 100 }')" -j DROP 2>/dev/null || true
}
trap cleanup EXIT

# netem only applies its reorder to packets it delays, so a small delay is needed for reordering.
if sudo tc qdisc add dev "$DEV" root netem delay 10ms reorder "$REORDER%" 50% loss "$LOSS%" duplicate "$DUP%" 2>/dev/null; then
  MODE=netem
  note "tc netem: $(tc qdisc show dev "$DEV" | head -1)"
elif sudo iptables -A OUTPUT -p udp -d "$TARGET_IP" --dport "$PORT" -m statistic --mode random \
      --probability "$(awk -v l="$LOSS" 'BEGIN { print l / 100 }')" -j DROP; then
  MODE=iptables
  DUP=0; REORDER=0
  note "netem is not available in this kernel; using iptables random drop (loss only)"
else
  fail "neither tc netem nor iptables statistic is available"
  finish; exit 1
fi

backend_mark
CLIENT_OUT="$EVIDENCE_DIR/03-client.txt"
iperf -u -c "$DOMAIN" -p "$PORT" -t "$DURATION" -b "$RATE" > "$CLIENT_OUT" 2>&1 || true
cleanup; MODE=""
sed 's/^/   | /' "$CLIENT_OUT"

STREAM=$(iperf_streams "$CLIENT_OUT" | head -1)
if [[ -z "$STREAM" || $(jq .total <<<"$STREAM") == null ]]; then
  # With loss injected, the FIN itself (and every retry) can be dropped, so occasionally no report arrives.
  fail "no server report (the injected loss may have hit the FIN and its retries; rerun)"
  finish; exit 1
fi
TOTAL=$(jq .total <<<"$STREAM"); LOST=$(jq .lost <<<"$STREAM")
PCT=$(awk -v l="$LOST" -v t="$TOTAL" 'BEGIN { printf "%.2f", 100 * l / t }')
# Three standard deviations of a binomial at this sample size, plus 0.5% for real internet loss.
TOL=$(awk -v n="$TOTAL" -v p="$LOSS" 'BEGIN { q = p / 100; printf "%.2f", 300 * sqrt(q * (1 - q) / n) + 0.5 }')
within "reported loss % matches the injected rate" "$LOSS" "$PCT" "$TOL"

EVENT=$(backend_find report "select(.Replay == false and .Datagrams == $TOTAL and .Lost == $LOST)") || true
if [[ -z "$EVENT" ]]; then
  fail "matching report event not found in the MicroVM's event log"
else
  note "backend: $(jq -c 'del(.ReportHex)' <<<"$EVENT")"
  save_evidence "03-report-event.json" "$EVENT"
  RECEIVED=$(( TOTAL - LOST ))
  DUPS=$(jq .Duplicates <<<"$EVENT")
  if [[ $DUP != 0 ]]; then
    DUP_PCT=$(awk -v d="$DUPS" -v r="$RECEIVED" 'BEGIN { printf "%.2f", 100 * d / r }')
    DTOL=$(awk -v n="$RECEIVED" -v p="$DUP" 'BEGIN { q = p / 100; printf "%.2f", 300 * sqrt(q * (1 - q) / n) + 0.2 }')
    within "duplicates detected at the injected rate (logged, not counted)" "$DUP" "$DUP_PCT" "$DTOL"
    # Every duplicate was excluded: bytes are exactly unique datagrams x length.
    LEN=$(( $(jq .Bytes <<<"$EVENT") / RECEIVED ))
    expect "bytes = unique datagrams x $LEN (no duplicate counted)" $(( RECEIVED * LEN )) "$(jq .Bytes <<<"$EVENT")"
  fi
  if [[ $REORDER != 0 ]]; then
    OOO=$(jq .OutOfOrder <<<"$EVENT")
    [[ $OOO -gt 0 ]] && pass "reordering observed: $OOO datagrams out of order" || fail "no reordering observed"
  fi
fi
finish
