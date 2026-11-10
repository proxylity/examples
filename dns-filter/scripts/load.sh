#!/bin/bash
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/configure.sh"
aws codebuild start-build --project-name "${STACK_NAME}-sync" --region "${AWS_REGION:?Set AWS_REGION to the home region}"
echo "Sync accepted. Inspect CodeBuild and SYNC#STATE for completion."
