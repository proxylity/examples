#!/usr/bin/env python3
"""
UDP Packet Latency Tester
Sends batches of UDP packets to an endpoint and measures latency by AWS region.
"""

import socket
import time
import argparse
import sys
from collections import defaultdict, deque
from threading import Lock
import uuid


class UDPLatencyTester:
    def __init__(self, host, port, num_packets=100, timeout=5.0):
        self.host = host
        self.port = port
        self.num_packets = num_packets
        self.timeout = timeout
        
        # Resolve DNS once for performance
        self.resolved_addr = (socket.gethostbyname(host), port)
        
        # Results storage
        self.responses = []
        self.responses_lock = Lock()
        self.total_received_count = 0
        self.send_failures = 0
        self.successful_sends = 0
        
        # Socket setup
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(timeout)
        
        # Increase send buffer size to prevent overflow with large batches
        # Default is typically 212992 bytes, we'll increase to 2MB
        try:
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 2 * 1024 * 1024)
        except Exception as e:
            print(f"Warning: Could not increase send buffer size: {e}")
        
    def send_packets(self):
        """Send UDP packets with timestamp tracking."""
        print(f"Sending {self.num_packets} packets to {self.host}:{self.port} ({self.resolved_addr[0]})...")
        
        send_times = []
        successful_sends = 0
        
        for i in range(self.num_packets):
            # Generate unique packet ID
            packet_id = str(uuid.uuid4())
            message = packet_id.encode('utf-8')
            
            # Record send time
            send_time = time.perf_counter()
            
            # Send packet
            try:
                bytes_sent = self.sock.sendto(message, self.resolved_addr)
                if bytes_sent == len(message):
                    send_times.append(send_time)
                    successful_sends += 1
                else:
                    self.send_failures += 1
                    print(f"Warning: Packet {i+1} partially sent ({bytes_sent}/{len(message)} bytes)")
            except socket.error as e:
                self.send_failures += 1
                if self.send_failures <= 10:
                    print(f"Error sending packet {i+1}: {e}")
                elif self.send_failures == 11:
                    print(f"(Suppressing further send errors...)")
            except Exception as e:
                self.send_failures += 1
                print(f"Unexpected error sending packet {i+1}: {e}")
        
        self.successful_sends = successful_sends
        print(f"Successfully sent {successful_sends}/{self.num_packets} packets to send buffer.")
        if self.send_failures > 0:
            print(f"Send failures: {self.send_failures}")
        
        return send_times
    
    def receive_responses(self, send_times):
        """Receive responses and calculate latencies."""
        print(f"Waiting for responses (timeout: {self.timeout}s)...")
        
        start_receive = time.perf_counter()
        available_send_times = deque(send_times)
        
        while True:
            try:
                # Check if we've exceeded overall timeout
                elapsed = time.perf_counter() - start_receive
                if elapsed > self.timeout:
                    break
                
                # Receive response
                data, addr = self.sock.recvfrom(1024)
                receive_time = time.perf_counter()
                
                # Parse response
                try:
                    response_str = data.decode('utf-8').strip()
                    parts = response_str.split(' ', 1)
                    
                    if len(parts) == 2:
                        count = int(parts[0])
                        region = parts[1]
                        
                        # Determine how many packets we can actually attribute
                        # (clamp to available send times to prevent over-counting)
                        attributable_count = min(count, len(available_send_times))
                        
                        with self.responses_lock:
                            self.total_received_count += attributable_count
                            
                            # Associate this response with 'attributable_count' packets
                            # Use FIFO order (earliest send times first)
                            packet_send_times = []
                            for _ in range(attributable_count):
                                if available_send_times:
                                    packet_send_times.append(available_send_times.popleft())
                            
                            # Calculate RTT for each packet
                            for pkt_send_time in packet_send_times:
                                latency_ms = (receive_time - pkt_send_time) * 1000
                                self.responses.append({
                                    'count': 1,
                                    'region': region,
                                    'latency_ms': latency_ms,
                                    'receive_time': receive_time
                                })
                            
                            # Warn if response claims more packets than we have
                            if count > attributable_count:
                                print(f"Warning: Response claims {count} packets but only {attributable_count} remain unaccounted")
                    
                except (ValueError, UnicodeDecodeError) as e:
                    print(f"Error parsing response: {e}")
                    
            except socket.timeout:
                break
            except Exception as e:
                print(f"Error receiving: {e}")
                break
    
    def calculate_statistics(self):
        """Calculate statistics by AWS region."""
        if not self.responses:
            print("\nNo responses received!")
            return
        
        # Group by region
        region_latencies = defaultdict(list)
        
        for response in self.responses:
            region = response['region']
            latency = response['latency_ms']
            region_latencies[region].append(latency)
        
        # Print results
        print("\n" + "="*60)
        print("RESULTS")
        print("="*60)
        
        print(f"\nPackets attempted: {self.num_packets}")
        if self.send_failures > 0:
            print(f"Send failures (never queued): {self.send_failures}")
        print(f"Packets successfully queued for send: {self.successful_sends}")
        print(f"Packets received (total from responses): {self.total_received_count}")
        
        # Calculate loss only from successfully sent packets
        packets_lost = self.successful_sends - self.total_received_count
        print(f"Packets lost in transit: {packets_lost}")
        
        if packets_lost > 0 and self.successful_sends > 0:
            loss_percentage = (packets_lost / self.successful_sends) * 100
            print(f"Packet loss rate: {loss_percentage:.2f}%")
        
        print("\n" + "-"*60)
        print("LATENCY BY AWS REGION")
        print("-"*60)
        
        for region in sorted(region_latencies.keys()):
            latencies = region_latencies[region]
            min_latency = min(latencies)
            max_latency = max(latencies)
            avg_latency = sum(latencies) / len(latencies)
            
            print(f"\nRegion: {region}")
            print(f"  Responses: {len(latencies)}")
            print(f"  Min latency: {min_latency:.2f} ms")
            print(f"  Max latency: {max_latency:.2f} ms")
            print(f"  Avg latency: {avg_latency:.2f} ms")
        
        print("\n" + "="*60)
    
    def run(self):
        """Run the UDP latency test."""
        try:
            send_times = self.send_packets()
            self.receive_responses(send_times)
            self.calculate_statistics()
        finally:
            self.sock.close()


def main():
    parser = argparse.ArgumentParser(
        description='UDP Packet Latency Tester - Measure latency by AWS region'
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
        help='Number of packets to send (default: 100)'
    )
    parser.add_argument(
        '-t', '--timeout',
        type=float,
        default=5.0,
        help='Timeout in seconds for receiving responses (default: 5.0)'
    )
    
    args = parser.parse_args()
    
    # Validate arguments
    if args.num_packets <= 0:
        print("Error: Number of packets must be positive")
        sys.exit(1)

    # Validate arguments
    if args.num_packets > 100:
        print("Error: Number of packets must be 100 or less")
        sys.exit(1)

    if args.timeout <= 0:
        print("Error: Timeout must be positive")
        sys.exit(1)
    
    # Run the test
    tester = UDPLatencyTester(
        host=args.host,
        port=args.port,
        num_packets=args.num_packets,
        timeout=args.timeout
    )
    
    tester.run()


if __name__ == '__main__':
    main()
