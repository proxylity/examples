#!/usr/bin/env bash
# Shows how a stock iperf2 client retries its FIN when no server report comes back:
# the proxy forwards to a port with no server, so every FIN goes unanswered.
cd "$(dirname "$0")"
python3 capture.py 5011 127.0.0.1 5999 /tmp/cap/fin.jsonl 20 &
PROXY=$!
sleep 0.5
iperf -u -c 127.0.0.1 -p 5011 -t 1 -b 100K > /tmp/cap/fin-client.txt 2>&1
kill $PROXY 2>/dev/null
tail -2 /tmp/cap/fin-client.txt
# Sequence (signed 32-bit low word) and time since the previous datagram, for the last 25 datagrams.
jq -r '[.t, .hex[0:8]] | @tsv' /tmp/cap/fin.jsonl | tail -25 | awk -F'\t' '
  { s = strtonum("0x" $2); if (s >= 2^31) s -= 2^32; printf "seq=%6d  dt=%7.1f ms\n", s, (NR>1 ? ($1-p)*1000 : 0); p = $1 }'
echo "== summary"
jq -r '[.t, .hex[0:8]] | @tsv' /tmp/cap/fin.jsonl | awk -F'\t' '
  { s = strtonum("0x" $2); if (s >= 2^31) s -= 2^32
    if (s < 0) { n++; if (!f) { f = $1; first = s }; last = s; l = $1 } else d++ }
  END { printf "data=%d fins=%d first=%d last=%d span=%.0f ms\n", d, n, first, last, (l - f) * 1000 }'
