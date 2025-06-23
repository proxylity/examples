#!/bin/bash
set -ex

# This script tears down the dns-filter example in all regions. It deletes the
# stacks created by the `scripts/deploy.sh` script. It is assumed that
# the stacks were created with the same name in each region.
#

#
# C O N F I G U R A T I O N
#
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"

# The stacks are removed in the following process:
# 1. TODO: Remove all objects from the DNS log S3 bucket in each region so the buckets can be removed.
# 2. Delete the regional stacks in us-west-2, us-east-1, and eu-west-1
# 3. Delete the global stack in us-west-2

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
 aws cloudformation delete-stack \
  --stack-name ${STACK_NAME} \
  --region ${DEPLOY_REGION} 
done

aws cloudformation delete-stack --stack-name ${STACK_NAME}-global --region ${AWS_REGION}