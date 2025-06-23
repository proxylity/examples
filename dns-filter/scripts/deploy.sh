#!/bin/bash
set -ex

# This script deploys the dns-filter example to multiple regions in a simple
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
# connects it to the regional destinations using a specific name (`stream-starter`).

aws cloudformation deploy \
    --template-file ./templates/dns-filter-global.template.json \
    --stack-name ${STACK_NAME}-global \
    --s3-bucket ${DEPLOY_BUCKET_NAME_PREFIX}${AWS_REGION} \
    --s3-prefix ${DEPLOY_BUCKET_PATH_PREFIX}${STACK_NAME} \
    --capabilities CAPABILITY_IAM \
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
    > ${STACK_NAME}-global.outputs

# Transform the stack output into JSON key/values to make them easier to consume
# in the regional template `Mappings`.
jq "[.Outputs[]|{(.OutputKey):.OutputValue}]|add" ${STACK_NAME}-global.outputs > global-stack-outputs.json


#
# D E P L O Y    T O    R E G I O N S
#

# Next, compile the regional stack template. This will be used to package and deploy
# the regional stacks, consuming the global stack outputs in `Mappings`.
sam build \
    --template-file ./templates/dns-filter-region.template.json

# Next deploy the app to 
for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
    # bucket name for the target region
    DEPLOY_BUCKET="${DEPLOY_BUCKET_NAME_PREFIX}${DEPLOY_REGION}"

    # prepare parameter overrides
    PARAMETER_OVERRIDES=""
    if [ ! -z "${DOMAIN_NAME}" ]; then
        PARAMETER_OVERRIDES="DomainName=${DOMAIN_NAME} HostedZoneId=${HOSTED_ZONE_ID}"
    fi
    if [ ! -z "${DNS_LOG_RETENTION}" ]; then
        PARAMETER_OVERRIDES="${PARAMETER_OVERRIDES} DnsLogRetentionDays=${DNS_LOG_RETENTION}"
    fi
    if [ ! -z "${PARAMETER_OVERRIDES}" ]; then
        PARAMETER_OVERRIDES="--parameter-overrides ${PARAMETER_OVERRIDES}"
    fi

    # deploy the stack
    sam deploy \
        --stack-name ${STACK_NAME} \
        --s3-bucket ${DEPLOY_BUCKET} \
        --s3-prefix ${DEPLOY_BUCKET_PATH_PREFIX}${STACK_NAME} \
        --capabilities CAPABILITY_IAM \
        --no-fail-on-empty-changeset \
        --region ${DEPLOY_REGION} \
        ${PARAMETER_OVERRIDES}
done
