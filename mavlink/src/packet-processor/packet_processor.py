"""SQS-triggered Lambda that decodes MAVLink v2 GPS_RAW_INT packets and
publishes decoded position events to an AppSync Events channel.

Each SQS message body is a hex-encoded MAVLink v2 frame. The function
extracts lat/lon/alt/vel/cog/fix/sats from msgid 24 (GPS_RAW_INT),
batches up to 5 events per AppSync publish call, and returns partial
batch failures so SQS can retry only the records that failed.
"""

import json
import logging
import os
import struct
import urllib.request

logger = logging.getLogger()
logger.setLevel(logging.INFO)

APPSYNC_HTTP_ENDPOINT = os.environ.get("APPSYNC_HTTP_ENDPOINT", "")
APPSYNC_API_KEY = os.environ.get("APPSYNC_API_KEY", "")
APPSYNC_CHANNEL = os.environ.get("APPSYNC_CHANNEL", "/drones/positions")
APPSYNC_URL = f"https://{APPSYNC_HTTP_ENDPOINT}/event"

MAVLINK_V2_STX = 0xFD
GPS_RAW_INT_ID = 24
# MAVLink v2 header is 10 bytes; GPS_RAW_INT payload needs at least 30 bytes
# (up to byte 39 for satellites_visible).
MIN_GPS_FRAME_LEN = 40

APPSYNC_BATCH_LIMIT = 5


def _decode_gps(data: bytes) -> dict | None:
    """Decode a MAVLink v2 GPS_RAW_INT frame into a position dict."""
    if len(data) < 10:
        logger.warning("Packet too short for MAVLink v2 header (%d bytes)", len(data))
        return None
    if data[0] != MAVLINK_V2_STX:
        logger.warning("Not a MAVLink v2 packet (STX=0x%02X)", data[0])
        return None

    sysid = data[5]
    compid = data[6]
    msgid = int.from_bytes(data[7:10], "little")

    if msgid != GPS_RAW_INT_ID:
        return None  # silently skip non-GPS messages

    if len(data) < MIN_GPS_FRAME_LEN:
        logger.warning("GPS_RAW_INT frame too short (%d bytes)", len(data))
        return None

    time_usec = struct.unpack_from("<Q", data, 10)[0]
    lat = struct.unpack_from("<i", data, 18)[0] / 1e7
    lon = struct.unpack_from("<i", data, 22)[0] / 1e7
    alt = struct.unpack_from("<i", data, 26)[0] / 1000.0
    vel = struct.unpack_from("<H", data, 34)[0] / 100.0
    cog = struct.unpack_from("<H", data, 36)[0] / 100.0
    fix_type = data[38]
    satellites = data[39]

    return {
        "deviceId": f"{sysid:02X}{compid:02X}",
        "lat": lat,
        "lon": lon,
        "alt": alt,
        "vel": vel,
        "cog": cog,
        "fix": fix_type,
        "sats": satellites,
        "ts": time_usec,
    }


def _publish_events(events: list[str]) -> None:
    """POST a batch of JSON event strings to AppSync Events."""
    body = json.dumps({"channel": APPSYNC_CHANNEL, "events": events}).encode()
    req = urllib.request.Request(
        APPSYNC_URL,
        data=body,
        headers={
            "Content-Type": "application/json",
            "x-api-key": APPSYNC_API_KEY,
        },
        method="POST",
    )
    with urllib.request.urlopen(req) as resp:
        logger.info("AppSync publish %d event(s): HTTP %s", len(events), resp.status)


def handler(event, context):
    """SQS Lambda handler -- processes a batch of MAVLink packets."""
    records = event.get("Records", [])
    failures: list[dict] = []
    pending_events: list[str] = []
    pending_ids: list[str] = []

    for record in records:
        msg_id = record["messageId"]
        try:
            data = bytes.fromhex(record["body"])
            position = _decode_gps(data)
            if position is None:
                continue  # non-GPS or malformed -- skip, not a failure
            pending_events.append(json.dumps(position))
            pending_ids.append(msg_id)

            if len(pending_events) >= APPSYNC_BATCH_LIMIT:
                _publish_events(pending_events)
                pending_events.clear()
                pending_ids.clear()

        except Exception:
            logger.exception("Failed to process record %s", msg_id)
            failures.append({"itemIdentifier": msg_id})

    # Flush remaining events
    if pending_events:
        try:
            _publish_events(pending_events)
        except Exception:
            logger.exception("Failed to publish final batch")
            failures.extend({"itemIdentifier": mid} for mid in pending_ids)

    return {"batchItemFailures": failures}
