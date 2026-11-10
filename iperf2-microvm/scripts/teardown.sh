#!/usr/bin/env bash
# Terminates the MicroVMs (started by Proxylity, so CloudFormation doesn't know about them) and deletes the stack.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/lib/common.sh"
load_outputs
for id in $(aws lambda-microvms list-microvms --image-identifier "$IMAGE_ARN" \
    --query "items[?state!='TERMINATED' && state!='TERMINATING'].microvmId" --output text); do
  echo "Terminating MicroVM $id"
  aws lambda-microvms terminate-microvm --microvm-identifier "$id" >/dev/null
done
echo "Deleting stack $STACK_NAME..."
aws cloudformation delete-stack --stack-name "$STACK_NAME"
aws cloudformation wait stack-delete-complete --stack-name "$STACK_NAME"
echo "Done. The artifact zips in S3 (iperf2-microvm/ prefix) are left in place."
