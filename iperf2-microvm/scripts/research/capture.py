#!/usr/bin/env python3
"""
Phase 0 research tool: a logging UDP proxy that sits between a stock iperf2 client and a
stock iperf2 server, so we can see exactly what bytes the protocol puts on the wire
(data datagrams, FIN datagrams, and the server report) without needing root/tcpdump.

Usage:
  iperf -s -u -p 5002 &
  python3 capture.py 5001 127.0.0.1 5002 capture.jsonl &
  iperf -u -c 127.0.0.1 -p 5001 -t 2 -b 1M
Each packet is written as one JSON line (direction, time, length, hex of the first 160 bytes).
"""
import json, select, socket, sys, time

listen_port, server_host, server_port, out_path = int(sys.argv[1]), sys.argv[2], int(sys.argv[3]), sys.argv[4]
duration = float(sys.argv[5]) if len(sys.argv) > 5 else 30

front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
front.bind(("127.0.0.1", listen_port))
back = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
client_addr = None
deadline = time.time() + duration

with open(out_path, "w") as out:
    while time.time() < deadline:
        ready, _, _ = select.select([front, back], [], [], 0.5)
        for sock in ready:
            data, addr = sock.recvfrom(65535)
            if sock is front:
                client_addr = addr
                back.sendto(data, (server_host, server_port))
                direction = "c2s"
            else:
                if client_addr:
                    front.sendto(data, client_addr)
                direction = "s2c"
            out.write(json.dumps({"dir": direction, "t": time.time(), "len": len(data),
                                  "hex": data[:160].hex()}) + "\n")
            out.flush()
