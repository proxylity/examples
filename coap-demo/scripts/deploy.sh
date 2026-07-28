#!/bin/bash
set -ex

# This script deploys the coap-demo example to multiple regions in a simple and
# demonstrative way. YMMV. Edit the environment variables in the configuration
# script (configure.sh) before running this one.

#
# C O N F I G U R A T I O N
#
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"

#
# D E P L O Y    G L O B A L    S T A C K
#

# The first deploy is the global stack. This stack creates the DynamoDB Global Table
# (replicated to every region in DEPLOY_TO_REGIONS) and the UDP Gateway Listener that
# every regional stack's RequestHandler Lambda will attach to.

aws cloudformation deploy \
    --template-file ./templates/global.template.json \
    --stack-name ${STACK_NAME}-global \
    --capabilities CAPABILITY_IAM \
    --parameter-overrides \
        ClientCidrToAllow="${ALLOWED_IPS}" \
    --no-fail-on-empty-changeset \
    --region ${AWS_REGION}

#
# C A P T U R E    G L O B A L    S T A C K    O U T P U T S
#

# Capture the outputs of the global stack and format them into a JSON object that is
# easily consumable in the `Mappings`/`Fn::FindInMap` of the regional stack template.
aws cloudformation describe-stacks \
    --stack-name ${STACK_NAME}-global \
    --query "Stacks[0]" \
    --output json \
    --region ${AWS_REGION} \
    > ${STACK_NAME}-global.outputs

jq "[.Outputs[]|{(.OutputKey):.OutputValue}]|add" ${STACK_NAME}-global.outputs > global-outputs.json

GLOBAL_TABLE_NAME=$(jq -r '.GlobalTableName' global-outputs.json)

#
# B U I L D
#

# Compile the regional stack template once; it is deployed unmodified to every region.
# NOTE: --parallel is intentionally omitted. RegionalAggregatorFunction and
# GlobalNotifierFunction both build directly against the shared CoapDemo.Common
# project (in place, not copied), and building them concurrently races on its
# obj/bin output and intermittently fails with MSB4018 (GenerateDepsFile).
sam build \
    --template-file ./templates/region.template.json

#
# D E P L O Y    T O    R E G I O N S
#

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
    # Each region's GlobalNotifier Lambda consumes that region's own replica of the
    # Global Table's stream. Per-replica stream ARNs aren't derivable via CloudFormation
    # Fn::GetAtt on AWS::DynamoDB::GlobalTable, so we look it up here and pass it in as a
    # parameter to that region's stack.
    GLOBAL_TABLE_STREAM_ARN=$(aws dynamodb describe-table \
        --table-name ${GLOBAL_TABLE_NAME} \
        --region ${DEPLOY_REGION} \
        --query 'Table.LatestStreamArn' \
        --output text)

    sam deploy \
        --stack-name ${STACK_NAME} \
        --resolve-s3 \
        --capabilities CAPABILITY_IAM \
        --no-fail-on-empty-changeset \
        --region ${DEPLOY_REGION} \
        --parameter-overrides \
            GlobalTableStreamArn="${GLOBAL_TABLE_STREAM_ARN}" \
            LambdaLogLevel="${LAMBDA_LOG_LEVEL}"
done
