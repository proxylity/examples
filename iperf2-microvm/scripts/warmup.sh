#!/usr/bin/env bash
# Makes sure the MicroVM is running and answering: sends a 2-datagram iperf2 test and waits for its report.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/lib/iperf2-packets.sh"
source "$HERE/lib/common.sh"
load_outputs
echo "Warming up $DOMAIN:$PORT (tenant $TENANT_ID)..."
if REPORT=$(warmup); then
  echo "MicroVM answered: $(report_json "$REPORT")"
else
  echo "No answer after 5 attempts. Check the delivery log group: $DELIVERY_LOG_GROUP" >&2
  exit 1
fi
