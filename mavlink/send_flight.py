#!/usr/bin/env python3
"""
send_flight.py -- Pure-Python MAVLink v2 flight simulator.

Simulates a fleet of drones flying circular paths and sends a realistic
mix of MAVLink v2 message types over UDP.  Zero external dependencies.

Each drone sends four messages per second:
  - HEARTBEAT     (msg  0) -- autopilot type, flight mode, system state
  - SYS_STATUS    (msg  1) -- battery voltage/current, sensor health
  - GPS_RAW_INT   (msg 24) -- latitude, longitude, altitude, speed
  - ATTITUDE      (msg 30) -- roll, pitch, yaw angles and rates

Usage:
    python send_flight.py --domain 127.0.0.1 --port 14550 --drones 3
    MAVLINK_DOMAIN=gw.example.com MAVLINK_PORT=14550 python send_flight.py
"""

import argparse
import math
import os
import random
import socket
import struct
import sys
import time

# ---------------------------------------------------------------------------
# MAVLink v2 constants
# ---------------------------------------------------------------------------
MAVLINK_STX = 0xFD
MAV_COMP_ID_UDP_BRIDGE = 0xC2  # 194

# Message IDs and CRC_EXTRA seeds (from MAVLink common.xml)
HEARTBEAT_ID      = 0;   HEARTBEAT_CRC_EXTRA      = 50
SYS_STATUS_ID     = 1;   SYS_STATUS_CRC_EXTRA     = 124
GPS_RAW_INT_ID    = 24;  GPS_RAW_INT_CRC_EXTRA    = 24
ATTITUDE_ID       = 30;  ATTITUDE_CRC_EXTRA       = 39

# HEARTBEAT constants
MAV_TYPE_QUADROTOR           = 2
MAV_AUTOPILOT_ARDUPILOTMEGA = 3
MAV_MODE_FLAG_GUIDED_ARMED   = 0x89  # CUSTOM_MODE_ENABLED | SAFETY_ARMED | GUIDED
MAV_STATE_ACTIVE             = 4
MAVLINK_VERSION              = 3

# SYS_STATUS sensor bitmask (GPS, IMU, barometer, magnetometer)
SENSOR_PRESENT = 0x0C27  # GPS, 3D_GYRO, 3D_ACCEL, 3D_MAG, ABS_PRESSURE

# GPS quality
GPS_FIX_3D   = 3
GPS_HDOP_CM  = 120   # 1.2 m
GPS_VDOP_CM  = 200   # 2.0 m
SATS_BASE    = 12
SATS_JITTER  = 3     # 12-15 visible satellites

# Flight profiles: (altitude_m, speed_m_s, initial_battery_pct)
FLIGHT_PROFILES = [
    (80,  8,  95),
    (120, 12, 88),
    (150, 15, 82),
]

# ---------------------------------------------------------------------------
# MAVLink X.25 CRC
# ---------------------------------------------------------------------------

def _crc_accumulate(byte, crc):
    """Accumulate one byte into an X.25 CRC."""
    tmp = (byte ^ (crc & 0xFF)) & 0xFF
    tmp = (tmp ^ ((tmp << 4) & 0xFF)) & 0xFF
    crc = ((crc >> 8) & 0xFF) ^ (tmp << 8) ^ (tmp << 3) ^ ((tmp >> 4) & 0xFF)
    return crc & 0xFFFF


def _mavlink_crc(buf, crc_extra):
    """CRC-16 over buf, then fold in the per-message CRC_EXTRA seed."""
    crc = 0xFFFF
    for b in buf:
        crc = _crc_accumulate(b, crc)
    crc = _crc_accumulate(crc_extra, crc)
    return crc

# ---------------------------------------------------------------------------
# Generic MAVLink v2 packet builder
# ---------------------------------------------------------------------------

def build_packet(sysid, compid, seq, msg_id, crc_extra, payload):
    """
    Build a complete MAVLink v2 packet from a pre-packed payload.

    Returns (packet_bytes, next_seq).
    """
    payload_len = len(payload)
    msg_id_bytes = struct.pack("<I", msg_id)[:3]  # 24-bit LE

    header = struct.pack(
        "BBBBBB",
        payload_len,     # byte 1
        0,               # byte 2: incompat flags
        0,               # byte 3: compat flags
        seq & 0xFF,      # byte 4: sequence
        sysid & 0xFF,    # byte 5: system id
        compid & 0xFF,   # byte 6: component id
    ) + msg_id_bytes     # bytes 7-9

    crc = _mavlink_crc(header + payload, crc_extra)

    packet = struct.pack("B", MAVLINK_STX) + header + payload + struct.pack("<H", crc)
    return packet, (seq + 1) & 0xFF

# ---------------------------------------------------------------------------
# Message builders
# ---------------------------------------------------------------------------

def build_heartbeat(sysid, compid, seq):
    """HEARTBEAT (msg 0): 9-byte payload -- <IBBBBB"""
    payload = struct.pack(
        "<IBBBBB",
        0,                                # custom_mode (none)
        MAV_TYPE_QUADROTOR,               # type
        MAV_AUTOPILOT_ARDUPILOTMEGA,      # autopilot
        MAV_MODE_FLAG_GUIDED_ARMED,       # base_mode
        MAV_STATE_ACTIVE,                 # system_status
        MAVLINK_VERSION,                  # mavlink_version
    )
    return build_packet(sysid, compid, seq, HEARTBEAT_ID, HEARTBEAT_CRC_EXTRA, payload)


def build_sys_status(sysid, compid, seq, voltage_mv, current_ca, battery_pct, cpu_load):
    """SYS_STATUS (msg 1): 31-byte payload -- <IIIHHhHHHHHHb"""
    payload = struct.pack(
        "<IIIHHhHHHHHHb",
        SENSOR_PRESENT,      # sensors present
        SENSOR_PRESENT,      # sensors enabled
        SENSOR_PRESENT,      # sensors health (all healthy)
        cpu_load,            # load (0-1000 = 0-100%)
        voltage_mv,          # voltage_battery (mV)
        current_ca,          # current_battery (cA, negative = discharging)
        0,                   # drop_rate_comm
        0, 0, 0, 0, 0,      # error counters
        battery_pct,         # battery_remaining (0-100)
    )
    return build_packet(sysid, compid, seq, SYS_STATUS_ID, SYS_STATUS_CRC_EXTRA, payload)


def build_gps_raw_int(sysid, compid, seq,
                      time_usec, lat_e7, lon_e7, alt_mm,
                      vel_cms, cog_cdeg, sats):
    """GPS_RAW_INT (msg 24): 30-byte payload -- <QiiiHHHHBB"""
    payload = struct.pack(
        "<QiiiHHHHBB",
        time_usec,
        lat_e7, lon_e7, alt_mm,
        GPS_HDOP_CM, GPS_VDOP_CM,
        vel_cms, cog_cdeg,
        GPS_FIX_3D, sats,
    )
    return build_packet(sysid, compid, seq, GPS_RAW_INT_ID, GPS_RAW_INT_CRC_EXTRA, payload)


def build_attitude(sysid, compid, seq,
                   time_boot_ms, roll, pitch, yaw,
                   rollspeed, pitchspeed, yawspeed):
    """ATTITUDE (msg 30): 28-byte payload -- <Iffffff"""
    payload = struct.pack(
        "<Iffffff",
        time_boot_ms,
        roll, pitch, yaw,
        rollspeed, pitchspeed, yawspeed,
    )
    return build_packet(sysid, compid, seq, ATTITUDE_ID, ATTITUDE_CRC_EXTRA, payload)

# ---------------------------------------------------------------------------
# Geo math -- circular orbit position
# ---------------------------------------------------------------------------
METERS_PER_DEG_LAT = 111320.0


def circular_position(center_lat, center_lon, radius_m, angle_rad):
    """
    Return (lat, lon) offset from center by radius_m at angle_rad.
    angle_rad=0 -> due north, increases clockwise (standard heading).
    """
    center_lat_rad = math.radians(center_lat)
    dlat = radius_m * math.cos(angle_rad) / METERS_PER_DEG_LAT
    dlon = radius_m * math.sin(angle_rad) / (METERS_PER_DEG_LAT * math.cos(center_lat_rad))
    return center_lat + dlat, center_lon + dlon


def heading_cdeg(angle_rad, speed):
    """Tangent heading for circular orbit. Returns centidegrees 0-35999."""
    if speed <= 0:
        return 0
    heading_rad = angle_rad + math.pi / 2.0
    heading_deg = math.degrees(heading_rad) % 360.0
    return int(heading_deg * 100.0) % 36000

# ---------------------------------------------------------------------------
# Per-drone state
# ---------------------------------------------------------------------------

class Drone:
    def __init__(self, sysid, profile_index, start_angle_rad,
                 center_lat, center_lon, radius_m):
        self.sysid = sysid
        self.compid = MAV_COMP_ID_UDP_BRIDGE
        self.seq = 0

        prof = FLIGHT_PROFILES[profile_index % len(FLIGHT_PROFILES)]
        self.alt_m = prof[0]
        self.speed_mps = prof[1]
        self.battery_pct = prof[2]

        self.center_lat = center_lat
        self.center_lon = center_lon
        self.radius_m = radius_m
        self.angle = start_angle_rad

        # Per-drone radius jitter: +/- 10% of base radius for path variation
        self.radius_m = radius_m * (1.0 + random.uniform(-0.10, 0.10))

        self.omega = self.speed_mps / self.radius_m if self.radius_m > 0 else 0.0

        self.boot_time = time.monotonic()
        self.tick = 0

    def step(self, dt):
        """Advance position and drain battery."""
        self.angle = (self.angle + self.omega * dt) % (2.0 * math.pi)
        self.tick += 1

        # Battery drain proportional to speed: base 0.015%/tick at 8 m/s,
        # scales linearly so a 15 m/s drone drains ~0.028%/tick.
        self.battery_pct = max(0, self.battery_pct - 0.015 * (self.speed_mps / 8.0))

    def _next_seq(self, pkt_seq):
        """Return packet and advance seq."""
        return pkt_seq

    def build_packets(self):
        """Build all four MAVLink v2 packets for one tick. Returns list of (packet, msg_name)."""
        packets = []

        now_usec = int(time.time() * 1e6)
        boot_ms = int((time.monotonic() - self.boot_time) * 1000) & 0xFFFFFFFF

        lat, lon = circular_position(
            self.center_lat, self.center_lon, self.radius_m, self.angle
        )
        lat_e7 = int(lat * 1e7)
        lon_e7 = int(lon * 1e7)
        alt_mm = int(self.alt_m * 1000)
        vel_cms = int(self.speed_mps * 100)
        cog = heading_cdeg(self.angle, self.speed_mps)
        sats = SATS_BASE + random.randint(0, SATS_JITTER)

        # Battery: voltage sags linearly with discharge (12.6V full -> 10.5V empty)
        voltage_mv = int(10500 + (12600 - 10500) * self.battery_pct / 100.0)
        current_ca = int(-self.speed_mps * 120)  # roughly proportional to speed
        cpu_load = 200 + random.randint(0, 50)    # 20-25%

        # Attitude: slight roll into the turn, yaw tracks heading
        yaw_rad = math.radians((heading_cdeg(self.angle, self.speed_mps) / 100.0))
        roll_rad = math.radians(5.0 * self.speed_mps / 15.0)  # bank angle
        pitch_rad = math.radians(-2.0)  # slight nose down
        yaw_rate = self.omega  # rad/s, same as angular velocity around the circle

        # 1. HEARTBEAT
        pkt, self.seq = build_heartbeat(self.sysid, self.compid, self.seq)
        packets.append((pkt, "HEARTBEAT"))

        # 2. SYS_STATUS
        pkt, self.seq = build_sys_status(
            self.sysid, self.compid, self.seq,
            voltage_mv, current_ca, int(self.battery_pct), cpu_load,
        )
        packets.append((pkt, "SYS_STATUS"))

        # 3. GPS_RAW_INT
        pkt, self.seq = build_gps_raw_int(
            self.sysid, self.compid, self.seq,
            now_usec, lat_e7, lon_e7, alt_mm,
            vel_cms, cog, sats,
        )
        packets.append((pkt, "GPS_RAW_INT"))

        # 4. ATTITUDE
        pkt, self.seq = build_attitude(
            self.sysid, self.compid, self.seq,
            boot_ms, roll_rad, pitch_rad, yaw_rad,
            0.0, 0.0, yaw_rate,
        )
        packets.append((pkt, "ATTITUDE"))

        return packets, lat, lon, sats

# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def parse_center(value):
    parts = value.split(",")
    if len(parts) != 2:
        raise argparse.ArgumentTypeError("center must be LAT,LON (e.g. 45.6951,-118.8434)")
    try:
        return float(parts[0]), float(parts[1])
    except ValueError:
        raise argparse.ArgumentTypeError("center coordinates must be numeric")


def parse_args():
    p = argparse.ArgumentParser(
        description="MAVLink v2 drone fleet simulator (pure Python, zero deps)"
    )
    p.add_argument("--domain", default=os.environ.get("MAVLINK_DOMAIN", "127.0.0.1"),
                   help="UDP destination host (env: MAVLINK_DOMAIN)")
    p.add_argument("--port", type=int, default=int(os.environ.get("MAVLINK_PORT", "14550")),
                   help="UDP destination port (env: MAVLINK_PORT)")
    p.add_argument("--drones", type=int, default=3,
                   help="Number of drones in the fleet (default: 3)")
    p.add_argument("--center", type=parse_center, default=(45.6951, -118.8434),
                   help="Orbit center as LAT,LON (default: 45.6951,-118.8434  Pendleton UAV Range)")
    p.add_argument("--radius", type=float, default=500.0,
                   help="Orbit radius in meters (default: 500)")
    return p.parse_args()

# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

def main():
    args = parse_args()
    center_lat, center_lon = args.center
    num_drones = args.drones
    radius_m = args.radius
    dest = (args.domain, args.port)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    drones = []
    for i in range(num_drones):
        start_angle = (2.0 * math.pi * i) / num_drones
        drones.append(Drone(
            sysid=i + 1, profile_index=i, start_angle_rad=start_angle,
            center_lat=center_lat, center_lon=center_lon, radius_m=radius_m,
        ))

    print("=" * 78)
    print("MAVLink v2 Flight Simulator")
    print("=" * 78)
    print("  Target  : {}:{}".format(dest[0], dest[1]))
    print("  Drones  : {}".format(num_drones))
    print("  Center  : {:.4f}, {:.4f}".format(center_lat, center_lon))
    print("  Radius  : {:.0f} m".format(radius_m))
    print("  Messages: HEARTBEAT, SYS_STATUS, GPS_RAW_INT, ATTITUDE (x{} drones = {} pkts/s)".format(
        num_drones, num_drones * 4))
    print("  Profiles:")
    for d in drones:
        print("    SYSID 0x{:02X} -- alt {}m, speed {}m/s, radius {:.0f}m, battery {}%".format(
            d.sysid, d.alt_m, d.speed_mps, d.radius_m, int(d.battery_pct)))
    print("-" * 78)
    print("  {:>6s}  {:>12s}  {:>13s}  {:>5s}  {:>5s}  {:>4s}  {:>5s}  {:>4s}".format(
        "SYSID", "LAT", "LON", "ALT", "VEL", "SATS", "BATT", "PKTS"))
    print("-" * 78)

    tick = 0
    total_pkts = 0
    try:
        while True:
            t0 = time.monotonic()

            for drone in drones:
                packets, lat, lon, sats = drone.build_packets()

                sent = 0
                for pkt, _name in packets:
                    try:
                        sock.sendto(pkt, dest)
                        sent += 1
                    except OSError as e:
                        if tick == 0 and sent == 0:
                            print("  [warn] sendto failed: {} (will keep trying)".format(e))

                total_pkts += sent

                print("  0x{:02X}    {:>12.7f}  {:>13.7f}  {:>4.0f}m  {:>4.1f}  {:>4d}  {:>4.0f}%  {:>4d}".format(
                    drone.sysid, lat, lon, drone.alt_m, drone.speed_mps,
                    sats, drone.battery_pct, sent))

                drone.step(1.0)

            tick += 1

            elapsed = time.monotonic() - t0
            if elapsed < 1.0:
                time.sleep(1.0 - elapsed)

    except KeyboardInterrupt:
        print("\n-- Ctrl+C received after {} ticks, {} packets sent.".format(tick, total_pkts))
    finally:
        sock.close()

    print("Done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
