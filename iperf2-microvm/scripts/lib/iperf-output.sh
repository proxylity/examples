#!/usr/bin/env bash
#
# Parses a stock iperf2 client's UDP output into one JSON object per stream:
#   {"id":1,"sent":1074,"jitterMs":0.037,"lost":0,"total":1073}
#
# The lines it reads look like this (iperf 2.1.x):
#   [  1] Sent 1074 datagrams
#   [  1] Server Report:
#   [  1] 0.0000-4.0013 sec  1.50 MBytes  3.15 Mbits/sec   0.037 ms 0/1073 (0%)
# A stream with no "Server Report" gets null jitter/lost/total.
iperf_streams() { # FILE
  awk '
    /\] Sent [0-9]+ datagrams/ {
      id = $0; sub(/^\[ */, "", id); sub(/\].*/, "", id)
      for (i = 1; i <= NF; i++) if ($i == "Sent") sent[id] = $(i + 1)
      if (id ~ /^[0-9]+$/) ids[id] = 1
    }
    /\] Server Report:/ { id = $0; sub(/^\[ */, "", id); sub(/\].*/, "", id); want[id] = 1; next }
    / ms +[0-9]+\/[0-9]+ / {
      id = $0; sub(/^\[ */, "", id); sub(/\].*/, "", id)
      if (!(id in want)) next
      for (i = 1; i <= NF; i++) if ($i == "ms") { jitter[id] = $(i - 1); split($(i + 1), lt, "/") }
      lost[id] = lt[1]; total[id] = lt[2]; delete want[id]
    }
    END {
      for (id in ids)
        printf "{\"id\":%s,\"sent\":%s,\"jitterMs\":%s,\"lost\":%s,\"total\":%s}\n", id, sent[id],
          ((id in jitter) ? jitter[id] : "null"), ((id in lost) ? lost[id] : "null"), ((id in total) ? total[id] : "null")
    }' "$1" | jq -s -c 'sort_by(.id)[]'
}
