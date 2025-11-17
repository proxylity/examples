#!/usr/bin/env python3
"""
Simple UDP Packet Counter
Sends UDP packets to an endpoint and displays responses with count totals by region.
"""

import socket
import time
import argparse
import sys
from collections import defaultdict


def send_packets_and_collect_responses(host, port, num_packets=100, timeout=5.0):
    """Send packets and collect all responses."""
    print(f"Sending {num_packets} packets to {host}:{port}...")
    
    # Determine address family (IPv4 or IPv6) by resolving the hostname
    try:
        addr_info = socket.getaddrinfo(host, port, socket.AF_UNSPEC, socket.SOCK_DGRAM)
        if not addr_info:
            raise Exception(f"Could not resolve hostname: {host}")
        
        # Use the first available address
        family, socktype, proto, canonname, sockaddr = addr_info[0]
        print(f"Using address family: {'IPv6' if family == socket.AF_INET6 else 'IPv4'}")
        print(f"Target address: {sockaddr[0]}")
        
        # Create socket with appropriate family
        sock = socket.socket(family, socket.SOCK_DGRAM)
        sock.settimeout(timeout)
        
    except Exception as e:
        raise Exception(f"Failed to resolve {host}:{port} - {e}")
    
    try:
        # Send packets
        for i in range(num_packets):
            message = f"packet_{i+1}".encode('utf-8')
            sock.sendto(message, sockaddr)
        
        # Record the time when the last packet was sent
        last_packet_sent_time = time.perf_counter()
        print(f"Sent {num_packets} packets. Waiting for responses...")
        
        # Collect responses
        responses = []
        region_counts = defaultdict(int)
        
        while True:
            try:
                data, addr = sock.recvfrom(1024)
                receive_time = time.perf_counter()
                elapsed_ms = (receive_time - last_packet_sent_time) * 1000
                payload = data.decode('utf-8').strip()
                
                # Display response details with elapsed time
                print(f"Response from {addr[0]}:{addr[1]} ({elapsed_ms:.2f}ms) - Payload: '{payload}'")
                
                # Parse response (expected format: "count region")
                try:
                    parts = payload.split(' ', 1)
                    if len(parts) == 2:
                        count = int(parts[0])
                        region = parts[1]
                        region_counts[region] += count
                        responses.append((count, region, addr))
                    else:
                        print(f"  Warning: Unexpected payload format")
                except ValueError:
                    print(f"  Warning: Could not parse count from payload")
                    
            except socket.timeout:
                break
            except Exception as e:
                print(f"Error receiving response: {e}")
                break
        
        return responses, region_counts
        
    finally:
        sock.close()


def display_summary(responses, region_counts):
    """Display summary of results."""
    print("\n" + "="*50)
    print("SUMMARY")
    print("="*50)
    
    if not responses:
        print("No responses received!")
        return
    
    print(f"Total responses received: {len(responses)}")
    
    print("\nPacket counts by region:")
    total_packets = 0
    for region in sorted(region_counts.keys()):
        count = region_counts[region]
        print(f"  {region}: {count} packets")
        total_packets += count
    
    print(f"\nTotal packets counted: {total_packets}")
    print("="*50)


def main():
    parser = argparse.ArgumentParser(
        description='Simple UDP Packet Counter - Send packets and display response counts by region'
    )
    parser.add_argument(
        'host',
        help='Target hostname or IP address'
    )
    parser.add_argument(
        'port',
        type=int,
        help='Target UDP port'
    )
    parser.add_argument(
        '-n', '--num-packets',
        type=int,
        default=100,
        help='Number of packets to send (default: 100, max: 100)'
    )
    parser.add_argument(
        '-t', '--timeout',
        type=float,
        default=5.0,
        help='Timeout in seconds for receiving responses (default: 5.0)'
    )
    
    args = parser.parse_args()
    
    # Validate arguments
    if args.num_packets <= 0 or args.num_packets > 100:
        print("Error: Number of packets must be between 1 and 100")
        sys.exit(1)
    
    if args.timeout <= 0:
        print("Error: Timeout must be positive")
        sys.exit(1)
    
    # Run the test
    try:
        responses, region_counts = send_packets_and_collect_responses(
            args.host, args.port, args.num_packets, args.timeout
        )
        display_summary(responses, region_counts)
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)


if __name__ == '__main__':
    main()
