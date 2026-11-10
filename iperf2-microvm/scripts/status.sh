#!/usr/bin/env bash
#
# Asks the running MicroVM what is in its memory: its ID (set by the /run hook), tenant, active and
# completed flows, and how many batches and packets it has processed. This talks to the MicroVM's own
# HTTPS endpoint with a short-lived token for port 8080, the same way Proxylity delivers packets.
#
# Usage: scripts/status.sh [MICROVM_ID]   (default: every RUNNING MicroVM from the image)
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"
load_outputs
IDS=${1:-$(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
  --query "items[?state=='RUNNING'].microvmId" --output text)}
for id in $IDS; do
  ENDPOINT=$(aws lambda-microvms get-microvm --microvm-identifier "$id" --query endpoint --output text)
  # The response is {"authToken": {"X-aws-proxy-auth": "<token>"}}: the header to send, and its value.
  TOKEN=$(aws lambda-microvms create-microvm-auth-token --microvm-identifier "$id" \
    --expiration-in-minutes 5 --allowed-ports '[{"port":8080}]' --output json | jq -r '.authToken["X-aws-proxy-auth"] // empty')
  [[ -n "$TOKEN" ]] || { echo "$id: could not get an auth token" >&2; continue; }
  [[ $ENDPOINT == http* ]] || ENDPOINT="https://$ENDPOINT"   # the API returns a bare host name
  printf '%s: ' "$id"
  curl -s --max-time 10 -H "X-aws-proxy-auth: $TOKEN" "${ENDPOINT%/}/" | jq -c . ; echo
done
