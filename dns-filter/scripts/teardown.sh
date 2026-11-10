#!/bin/bash
set -euo pipefail

# This script tears down the dns-filter example in all regions. It deletes the
# stacks created by the `scripts/deploy.sh` script. It is assumed that
# the stacks were created with the same name in each region.
#

#
# C O N F I G U R A T I O N
#
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"
for CONTROL_STACK in "${STACK_NAME}-admin" "${STACK_NAME}-sync"; do
  aws cloudformation delete-stack --stack-name "$CONTROL_STACK" --region "$AWS_REGION"
  aws cloudformation wait stack-delete-complete --stack-name "$CONTROL_STACK" --region "$AWS_REGION"
done

# The stacks are removed in the following process:
# 1. Optionally empty the DNS data (query log) S3 bucket in each region. The
#    buckets are retained by the stacks either way; emptying lets you delete them
#    afterward without extra steps. You are asked before anything is deleted.
# 2. Delete the regional stacks in us-west-2, us-east-1, and eu-west-1
# 3. Delete the global stack in us-west-2
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
  DATA_BUCKET="dns-data-${ACCOUNT_ID}-${DEPLOY_REGION}"
  if ! aws s3api head-bucket --bucket "$DATA_BUCKET" --region "$DEPLOY_REGION" 2>/dev/null; then
    continue
  fi
  HAS_OBJECTS=$(aws s3api list-objects-v2 --bucket "$DATA_BUCKET" --region "$DEPLOY_REGION" \
    --max-items 1 --query 'length(Contents || `[]`)' --output text)
  if [[ "$HAS_OBJECTS" != "0" ]]; then
    if [[ -t 0 ]]; then
      read -r -p "Bucket $DATA_BUCKET is not empty. Permanently delete all objects in it? [y/N] " ANSWER
      if [[ "$ANSWER" =~ ^[Yy]$ ]]; then
        aws s3 rm "s3://${DATA_BUCKET}" --recursive --region "$DEPLOY_REGION"
      else
        echo "Leaving $DATA_BUCKET as is."
      fi
    else
      echo "Bucket $DATA_BUCKET is not empty; skipping (no terminal to ask for confirmation)." >&2
    fi
  fi
done

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
 aws cloudformation delete-stack \
  --stack-name ${STACK_NAME} \
  --region ${DEPLOY_REGION} 
 aws cloudformation wait stack-delete-complete --stack-name "$STACK_NAME" --region "$DEPLOY_REGION"
done

aws cloudformation delete-stack --stack-name ${STACK_NAME}-global --region ${AWS_REGION}
echo "Data tables, data/artifact buckets, user pools, and KMS keys are retained. Purge only after deliberate review."