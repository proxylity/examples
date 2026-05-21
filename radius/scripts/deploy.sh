#!/bin/bash
set -ex

# This script deploys the radius example to multiple regions in a simple
# and demonstrative way. YMMV. Edit the environment variables in the configuration
# script (configure.sh) before running this one.

#
# C O N F I G U R A T I O N
#
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"

#
# D E P L O Y    G L O B A L    S T A C K
#

# The first deploy is the global stack. This stack creates the the listener and
# IAM roles that will be used throughout the solution.

aws cloudformation deploy \
    --template-file ./templates/global.template.json \
    --stack-name ${STACK_NAME}-global \
    --capabilities CAPABILITY_IAM \
    --parameter-overrides \
        ClientCidrToAllow="${ALLOWED_IPS}" \
        Transport="${TRANSPORT}" \
        FirstPeerPublicKey="${PEER_PUBLIC_KEY}" \
    --no-fail-on-empty-changeset \
    --region ${AWS_REGION}

#
# C A P T U R E    G L O B A L    S T A C K    O U T P U T S
#

# Capture the outputs of the global stack and format them into a JSON object that
# is easily consumable in the `Mappings`/`Fn::FindInMap` of the regional stack
# templates.
aws cloudformation describe-stacks \
    --stack-name ${STACK_NAME}-global \
    --query "Stacks[0]" \
    --output json \
    --region ${AWS_REGION} \
    > ${STACK_NAME}-global.outputs

# Transform the stack output into JSON key/values to make them easier to consume
# in the regional template `Mappings`.
jq "[.Outputs[]|{(.OutputKey):.OutputValue}]|add" ${STACK_NAME}-global.outputs > global-outputs.json


#
# D E P L O Y    T O    R E G I O N S
#

# Next, compile the regional stack template. This will be used to package and deploy
# the regional stacks, consuming the global stack outputs in `Mappings`.
sam build \
    --parallel \
    --template-file ./templates/region.template.json

# Next deploy the app to 
for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
    # deploy the stack
    sam deploy \
        --stack-name ${STACK_NAME} \
        --resolve-s3 \
        --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
        --no-fail-on-empty-changeset \
        --region ${DEPLOY_REGION} \
        --parameter-overrides \
            RadiusSharedSecret="${RADIUS_SHARED_SECRET}" \
            DeployedRegions="$(echo ${DEPLOY_TO_REGIONS} | tr ' ' ',')"
done
