#!/bin/bash
set -ex

# This script tears down the coap-demo example in all regions. It deletes the
# stacks created by the `scripts/deploy.sh` script. It is assumed that the
# stacks were created with the same name in each region.
#

#
# C O N F I G U R A T I O N
#
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"

# The stacks are removed in the following order:
# 1. Delete the regional stacks (Lambda functions, regional DynamoDB tables, SNS topic,
#    Packet Source, EventBridge Scheduler group) in each deployed region.
# 2. Delete the global stack (DynamoDB Global Table and its replicas, and the UDP Gateway
#    Listener) in ${AWS_REGION}.

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
  aws cloudformation delete-stack \
    --stack-name ${STACK_NAME} \
    --region ${DEPLOY_REGION}
done

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
  aws cloudformation wait stack-delete-complete \
    --stack-name ${STACK_NAME} \
    --region ${DEPLOY_REGION}
done

aws cloudformation delete-stack --stack-name ${STACK_NAME}-global --region ${AWS_REGION}
aws cloudformation wait stack-delete-complete --stack-name ${STACK_NAME}-global --region ${AWS_REGION}

rm -f global-outputs.json ${STACK_NAME}-global.outputs
