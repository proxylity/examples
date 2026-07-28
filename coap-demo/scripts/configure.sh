#!/bin/bash
set -ex

#
# R E G I O N S
#

# The regions that will host handlers for the coap-demo example. The AWS_REGION
# environment variable is used to deploy the global stack, and the DEPLOY_TO_REGIONS
# environment variable is used to deploy the regional stacks. The value of AWS_REGION
# must be one of the regions in DEPLOY_TO_REGIONS.
#
# NOTE: Editing the regions here will not change the regions in the global stack
# template. You will need to edit the `Replicas` list of the `GlobalTable` resource in
# `templates/global.template.json` to change the regions the DynamoDB Global Table is
# replicated to, to match.
export DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"
export AWS_REGION="${AWS_REGION:-us-west-2}"

#
# C O N F I G U R A T I O N
#

# Security first! To restrict use of the CoAP listener to a specific set of IP
# addresses, set the `ALLOWED_IPS` environment variable to the CIDR notation of the
# allowed IP addresses. The default is to restrict access to the current public IP
# (probably your internet gateway, so it will only be accessible from your network).
# To allow open/unrestricted access, set this to 0.0.0.0/0.
export ALLOWED_IPS="${ALLOWED_IPS:-$(curl -s checkip.amazonaws.com)/32}"

# The name of the stack to deploy. The global stack is named "${STACK_NAME}-global"
# and each regional stack uses this same name (deployed once per region).
export STACK_NAME="${STACK_NAME:-coap-demo}"

# Log level for all Lambda functions.
export LAMBDA_LOG_LEVEL="${LAMBDA_LOG_LEVEL:-INFO}"
