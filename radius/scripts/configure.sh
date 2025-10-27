#!/bin/bash
set -ex

#
# R E G I O N S
#

# The regions that will host handlers for the radius example. The AWS_REGION
# environment variable is used to deploy the global stack, and the DEPLOY_TO_REGIONS
# environment variable is used to deploy the regional stacks. The value of AWS_REGION
# must be one of the regions in DEPLOY_TO_REGIONS. 
# 
# NOTE: Editing the regions here will not change the regions in the global stack template.
# You will need to edit the `global.template.json` file to change the regions in which
# the DDB global table is replicated to match.
export DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"


#
# C O N F I G U R A T I O N
#

# Security first! To restrict use of the UDP and DoH services to a specific set of IP
# addresses, set the `ALLOWED_IPS` environment variable to the CIDR notation of the
# allowed IP addresses. The default is to restrict access to the current public IP (
# probably your internet gateway, so it will only be accessible from your network).
# To allow open/unrestricted access, set this to 0.0.0.0/0.
export ALLOWED_IPS="${ALLOWED_IPS:-$(curl -s checkip.amazonaws.com)/32}"

# The prefix of the bucket name to use for deployment artifacts. This will be suffixed
# with the region name, and a bucket with the resuling name must exist in each deployment
# region (due to the way CloudFormation/SAM works). You can run `scripts/prerequisites.sh`
# to find or create a set of buckets with the correct prefix in each region.
export DEPLOY_BUCKET_NAME_PREFIX="${DEPLOY_BUCKET_NAME_PREFIX:-_replace_with_a_valid_prefix_}"

# The S3 path prefix to use for deployment artifacts. The `cloudformation deploy` and
# `sam deploy` commands will use this prefix to store artifacts used in the respective
# stacks.
export DEPLOY_BUCKET_PATH_PREFIX="${DEPLOY_BUCKET_PATH_PREFIX:-builds}"

# The name of the stack to deploy.
export STACK_NAME="${STACK_NAME:-radius}"
