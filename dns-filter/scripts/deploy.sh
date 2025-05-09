#!/bin/bash
set -ex

# This script deploys the dns-filter example to multiple regions in a simple
# and demonstrative way. YMMV. Edit the environment variables in the configuration
# section to suit your needs.

#
# C O N F I G U R A T I O N
#

# The regions that will host handlers for the dns-filter example. The global
# stack will be deployed to us-west-2. 
DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"

# The prefix of the bucket name to use for deployment artifacts. This will be suffixed
# with the region name, and a bucket with the resuling name must exist in each deployment
# region (due to the way CloudFormation/SAM works).   
DEPLOY_BUCKET_NAME_PREFIX="cpdev-"

# The S3 path prefix to use for deployment artifacts. The `cloudformation deploy` and
# `sam deploy` commands will use this prefix to store artifacts used in the respective
# stacks.
DEPLOY_BUCKET_PATH_PREFIX="builds/"

# The name of the stack to deploy.
STACK_NAME="dns-filter"

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

    # deploy the stack
    sam deploy \
        --stack-name ${STACK_NAME} \
        --s3-bucket ${DEPLOY_BUCKET} \
        --s3-prefix ${DEPLOY_BUCKET_PATH_PREFIX}${STACK_NAME} \
        --capabilities CAPABILITY_IAM \
        --no-fail-on-empty-changeset \
        --region ${DEPLOY_REGION}
done
