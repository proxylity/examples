#!/usr/bin/env python3
"""Send test and demo syslog traffic through the WireGuard tunnel.

Standard library only. Run it on any host whose WireGuard peer for the syslog listener is up
(a site router, or a laptop configured as a site).

  python3 send-test-messages.py                           # send the test set to the collector address
  python3 send-test-messages.py --burst 500 --rate 50     # also send 500 info messages at 50/s

The test set is one RFC 5424 message per severity (0-7), one RFC 3164 message, one RFC 5424
message with structured data and escapes, and one message that isn't syslog. Every message carries
a run ID so you can find it in CloudWatch Logs, Athena and the archive.
"""

import argparse
import datetime
import json
import os
import re
import socket
import sys
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG_DIR = os.path.dirname(HERE)

SEVERITIES = ["emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"]
FACILITY_LOCAL0 = 16
FACILITY_AUTHPRIV = 10
SD_ID = "test@32473"
MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_config(config_dir):
    return load_json(os.path.join(config_dir, "syslog.config.json"))


# ---------------------------------------------------------------------------------------------
# Messages
# ---------------------------------------------------------------------------------------------

def ascii_token(value, fallback, limit):
    token = re.sub(r"[^!-~]", "", value or "")[:limit]
    return token or fallback


def rfc5424(pri, hostname, app, procid, msgid, sd, message):
    now = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    return f"<{pri}>1 {now} {hostname} {app} {procid} {msgid} {sd} {message}"


def rfc3164(pri, hostname, tag, pid, message):
    now = datetime.datetime.now(datetime.timezone.utc)
    stamp = f"{MONTHS[now.month - 1]} {now.day:2d} {now:%H:%M:%S}"
    return f"<{pri}>{stamp} {hostname} {tag}[{pid}]: {message}"


def build_test_set(run_id, hostname, include_unparseable=True):
    app = "syslog-test"
    procid = str(os.getpid())
    messages = []
    for severity, name in enumerate(SEVERITIES):
        sd = f'[{SD_ID} run="{run_id}" seq="{severity}"]'
        messages.append((name, rfc5424(FACILITY_LOCAL0 * 8 + severity, hostname, app, procid, "SEV", sd,
                                        f"run={run_id} test message with severity {name}")))
    messages.append(("info (rfc3164)", rfc3164(FACILITY_AUTHPRIV * 8 + 6, hostname, "sshd", procid,
                                               f"run={run_id} RFC 3164 test message")))
    sd = (f'[{SD_ID} run="{run_id}" seq="8" note="quote \\" bracket \\] backslash \\\\"]'
          '[origin ip="127.0.0.1" software="send-test-messages.py"]')
    messages.append(("notice (structured data)", rfc5424(FACILITY_LOCAL0 * 8 + 5, hostname, app, procid, "SD", sd,
                                                         f"run={run_id} RFC 5424 test message with structured data")))
    if include_unparseable:
        messages.append(("unparseable", f"this is not syslog run={run_id}"))
    return messages


def expected_counts(messages, burst):
    severities = {}
    for label, _ in messages:
        name = label.split(" ")[0]
        if name in SEVERITIES:
            severities[name] = severities.get(name, 0) + 1
    if burst:
        severities["info"] = severities.get("info", 0) + burst
    total = len(messages) + burst
    return {
        "MessageCount": total,
        "SiteMessageCount (this site)": total,
        "HighSeverityCount": sum(severities.get(s, 0) for s in SEVERITIES[:3]),
        "ParseFailureCount": sum(1 for label, _ in messages if label == "unparseable"),
        "SeverityCount": severities,
    }


def send(sock, address, text):
    data = text.encode("utf-8")
    if len(data) > 1200:
        raise SystemExit(f"Message is {len(data)} bytes; keep test messages under the tunnel MTU.")
    sock.sendto(data, address)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config-dir", default=DEFAULT_CONFIG_DIR,
                        help="Directory with syslog.config.json (default: the example directory).")
    parser.add_argument("--host", help="Collector address in the tunnel (default: Network.CollectorAddress).")
    parser.add_argument("--port", type=int, default=514)
    parser.add_argument("--hostname", default=socket.gethostname(), help="HOSTNAME field to send.")
    parser.add_argument("--run-id", default=uuid.uuid4().hex[:12], help="Correlation ID put in every message.")
    parser.add_argument("--burst", type=int, default=0, help="Number of extra info messages to send.")
    parser.add_argument("--rate", type=float, default=20.0, help="Burst rate in messages per second.")
    parser.add_argument("--delay", type=float, default=0.05, help="Seconds between test-set messages.")
    parser.add_argument("--skip-unparseable", action="store_true",
                        help="Don't send the non-syslog message. It's also reported as one destination error on the "
                             "logs and analytics destinations, so it trips their Errors alarms.")
    args = parser.parse_args()

    config = load_config(args.config_dir)
    host = args.host or config.get("Network", {}).get("CollectorAddress")
    if not host:
        raise SystemExit("No --host given and Network.CollectorAddress isn't set.")
    hostname = ascii_token(args.hostname, "test-host", 255)
    address = (host, args.port)
    family = socket.AF_INET6 if ":" in host else socket.AF_INET
    messages = build_test_set(args.run_id, hostname, not args.skip_unparseable)

    with socket.socket(family, socket.SOCK_DGRAM) as sock:
        for label, text in messages:
            send(sock, address, text)
            print(f"sent {label:26} {text}")
            time.sleep(args.delay)
        if args.burst:
            interval = 1.0 / args.rate if args.rate > 0 else 0
            start = time.monotonic()
            for i in range(args.burst):
                sd = f'[{SD_ID} run="{args.run_id}" seq="burst-{i}"]'
                send(sock, address, rfc5424(FACILITY_LOCAL0 * 8 + 6, hostname, "syslog-test", str(os.getpid()),
                                            "BURST", sd, f"run={args.run_id} burst message {i + 1} of {args.burst}"))
                if interval:
                    time.sleep(max(0.0, start + (i + 1) * interval - time.monotonic()))
            print(f"sent {args.burst} burst messages in {time.monotonic() - start:.1f} s")

    counts = expected_counts(messages, args.burst)
    print()
    print(f"Run ID: {args.run_id}  (sent to {host}:{args.port})")
    print("Expected metric increments in the syslog namespace (if nothing was lost and no logs FilterExpression is set):")
    for name, value in counts.items():
        print(f"  {name}: {value}")
    if counts["ParseFailureCount"]:
        print("  Proxylity Errors: 1 on each of the logs and analytics destinations (the unparseable message)")
    print()
    print("Find the messages in Logs Insights (logs group):")
    print(f"  fields ReceivedAt, Remote.PeerKey, Inner.SourceAddress, Data.severity_name, Data.message, Data.raw")
    print(f"  | filter Data.message like /{args.run_id}/ or Data.raw like /{args.run_id}/ | sort ReceivedAt asc")
    print("Find them in the archive (Athena, after the 5 minute Firehose buffer):")
    print(f"  SELECT receivedat, from_utf8(from_base64(data)) FROM syslog_archive")
    print(f"  WHERE dt = format_datetime(current_timestamp, 'yyyy-MM-dd') AND strpos(from_utf8(from_base64(data)), '{args.run_id}') > 0;")
    return 0


if __name__ == "__main__":
    sys.exit(main())
