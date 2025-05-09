#!/bin/bash
set -ex

# This script tears down the dns-filter example in all regions. It deletes the
# stacks created by the `scripts/deploy.sh` script. It is assumed that
# the stacks were created with the same name in each region.
#
# The script will delete the stacks in the following order:
# 1. Delete the regional stacks in us-west-2, us-east-1, and eu-west-1
# 2. Delete the global stack in us-west-2

for region in us-west-2 us-east-1 eu-west-1; do aws cloudformation delete-stack \
  --stack-name dns-filter \
  --region $region; done

aws cloudformation delete-stack --stack-name dns-filter-global --region us-west-2