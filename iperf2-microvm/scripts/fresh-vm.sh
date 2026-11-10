#!/usr/bin/env bash
#
# Starts over with a brand new MicroVM (empty memory) by changing the destination's TenantId.
#
# The destination uses a fixed TenantId, so every test shares one MicroVM. Proxylity starts one
# MicroVM per tenant, so a new tenant ID means the next packet starts a new MicroVM. No code changes
# or image rebuilds are needed: only the listener's destination argument changes.
#
# Usage: scripts/fresh-vm.sh [NEW_TENANT_ID]     (default: bump the trailing number, iperf2-1 -> iperf2-2)
#   KEEP_OLD=1   leave the previous MicroVMs running (by default they are terminated to save cost)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/lib/iperf2-packets.sh"
source "$HERE/lib/common.sh"
load_outputs

OLD=$TENANT_ID
if [[ $# -ge 1 ]]; then
  NEW=$1
elif [[ $OLD =~ ^(.*-)([0-9]+)$ ]]; then
  NEW="${BASH_REMATCH[1]}$(( BASH_REMATCH[2] + 1 ))"
else
  NEW="$OLD-2"
fi
echo "Switching tenant $OLD -> $NEW"

if [[ "${KEEP_OLD:-0}" != 1 ]]; then
  # MicroVMs are started by Proxylity, not CloudFormation; they hold only test state.
  for id in $(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
      --query "items[?state!='TERMINATED' && state!='TERMINATING'].microvmId" --output text); do
    echo "Terminating old MicroVM $id"
    aws lambda-microvms terminate-microvm --microvm-identifier "$id" >/dev/null
  done
fi

# Same template, every parameter unchanged except TenantId.
PARAMS=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" --query 'Stacks[0].Parameters' --output json |
  jq -c --arg t "$NEW" 'map(if .ParameterKey == "TenantId"
                             then {ParameterKey, ParameterValue: $t}
                             else {ParameterKey, UsePreviousValue: true} end)')
aws cloudformation update-stack --stack-name "$STACK_NAME" --use-previous-template \
  --parameters "$PARAMS" --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND >/dev/null
echo "Waiting for the stack update..."
aws cloudformation wait stack-update-complete --stack-name "$STACK_NAME"

load_outputs
echo "Tenant is now $TENANT_ID. Starting its MicroVM..."
warmup >/dev/null || { echo "The new MicroVM did not answer" >&2; exit 1; }
backend_connect
for id in "${!_VM_TOKEN[@]}"; do
  echo "Running: $(backend_get "$id" /status | jq -c "{Microvm, Tenant, ActiveFlows, Batches, Packets}")"
done
