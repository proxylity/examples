#!/usr/bin/env python3
import os
import socket
import random
import time
from datetime import datetime

DOMAIN = os.environ.get('MAVLINK_DOMAIN')
PORT = os.environ.get('MAVLINK_PORT')

if not DOMAIN or not PORT:
    print("Error: MAVLINK_DOMAIN and MAVLINK_PORT environment variables must be set")
    exit(1)

PORT = int(PORT)
seq1 = 0
seq2 = 0

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# Pre-built MAVLink v1 HEARTBEAT (seq byte at index 2)
v1_packet = bytearray.fromhex('FE 09 00 A2 C2 00 06 08 40 00 00 00 00 04 03 97 83')

# Pre-built MAVLink v2 HEARTBEAT (seq byte at index 4)
v2_packet = bytearray.fromhex('FD 09 00 00 00 C2 E2 00 00 00 06 08 40 00 00 00 00 04 03 C4 4F')

print(f"Sending HEARTBEAT packets to {DOMAIN}:{PORT}")
print("Press Ctrl+C to exit\n")

while True:
    try:
        v1_packet[2] = seq1
        seq1 = (seq1 + 1) % 256

        v2_packet[4] = seq2
        seq2 = (seq2 + 1) % 256

        hex_str1 = ' '.join(f'{b:02X}' for b in v1_packet)
        hex_str2 = ' '.join(f'{b:02X}' for b in v2_packet)

        print(f"[{datetime.now().strftime('%H:%M:%S')}] v1 seq={seq1:3d}: {hex_str1}")
        print(f"[{datetime.now().strftime('%H:%M:%S')}] v2 seq={seq2:3d}: {hex_str2}")

        sock.sendto(v1_packet, (DOMAIN, PORT))
        sock.sendto(v2_packet, (DOMAIN, PORT))

        time.sleep(1)
    
    except KeyboardInterrupt:
        print("\nExiting...")
        break
    except Exception as e:
        print(f"Error: {e}")

sock.close()
