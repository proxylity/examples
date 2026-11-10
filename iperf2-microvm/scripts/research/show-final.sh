#!/usr/bin/env bash
# Prints the FIN datagram(s) and server report from /tmp/cap/cap.jsonl (first 160 bytes of each).
jq -r 'select(.dir=="s2c" or (.hex|startswith("ffffff"))) | .dir+" len="+(.len|tostring)+"\n"+.hex' /tmp/cap/cap.jsonl | fold -w 64
