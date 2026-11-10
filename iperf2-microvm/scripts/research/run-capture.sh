#!/usr/bin/env bash
# Runs a stock iperf2 client against a stock iperf2 server through the logging proxy.
# Output goes to /tmp/cap (client.txt, server.txt, cap.jsonl).
set -u
cd "$(dirname "$0")"
OUT=/tmp/cap; mkdir -p "$OUT"
ARGS=("$@"); [ ${#ARGS[@]} -eq 0 ] && ARGS=(-t 2 -b 1M)

iperf -s -u -p 5002 > $OUT/server.txt 2>&1 &
SERVER=$!
sleep 0.5
python3 capture.py 5001 127.0.0.1 5002 $OUT/cap.jsonl 12 &
PROXY=$!
sleep 0.5
iperf -u -c 127.0.0.1 -p 5001 "${ARGS[@]}" > $OUT/client.txt 2>&1
sleep 1
kill $SERVER $PROXY 2>/dev/null
echo "== client"; cat $OUT/client.txt
echo "== server"; cat $OUT/server.txt
echo "== packets: $(wc -l < $OUT/cap.jsonl)"
