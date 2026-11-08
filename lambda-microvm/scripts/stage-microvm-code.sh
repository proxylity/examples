#!/usr/bin/env bash
#
# Bridges a gap in the current SAM CLI: `sam package`/`sam deploy` can't yet upload a local
# CodeUri for AWS::Serverless::MicrovmImage (tracked in aws/aws-sam-cli#9283). Run this after
# `sam build` and before `sam deploy`. It zips the MicroVM source directory, uploads it to S3,
# and points the built template's CodeUri at the uploaded object.
#
# Usage: scripts/stage-microvm-code.sh [s3-bucket]
#   s3-bucket  Bucket in the deployment region. Defaults to the SAM CLI managed bucket.
#
# Once SAM CLI packages MicrovmImage code natively, this script is no longer needed.

set -euo pipefail

cd "$(dirname "$0")/.."

SOURCE_DIR="src/echo-microvm"
BUILT_TEMPLATE=".aws-sam/build/template.yaml"
PREFIX="lambda-microvm-example"

if [[ ! -f "${BUILT_TEMPLATE}" ]]; then
  echo "Built template not found at ${BUILT_TEMPLATE}. Run 'sam build' first." >&2
  exit 1
fi

BUCKET="${1:-}"
if [[ -z "${BUCKET}" ]]; then
  BUCKET=$(aws cloudformation describe-stacks \
    --stack-name aws-sam-cli-managed-default \
    --query "Stacks[0].Outputs[?OutputKey=='SourceBucket'].OutputValue" \
    --output text 2>/dev/null || true)
  if [[ -z "${BUCKET}" || "${BUCKET}" == "None" ]]; then
    echo "No SAM managed bucket found. Run 'sam deploy --guided' once, or pass a bucket name." >&2
    exit 1
  fi
fi

# Key the object on a hash of the source files so CloudFormation sees a change (and the
# image is rebuilt) only when the MicroVM code changes.
HASH=$(find "${SOURCE_DIR}" -type f -print0 | sort -z | xargs -0 cat | sha256sum | cut -c1-16)
ZIP_FILE=".aws-sam/echo-microvm-${HASH}.zip"
KEY="${PREFIX}/echo-microvm-${HASH}.zip"

rm -f "${ZIP_FILE}"
(cd "${SOURCE_DIR}" && zip -q -r -X "../../${ZIP_FILE}" .)

aws s3 cp "${ZIP_FILE}" "s3://${BUCKET}/${KEY}" --only-show-errors

# The template has a single CodeUri (the MicroVM image), so a line-level replace is safe.
sed -i.bak -E "s#^([[:space:]]*CodeUri:).*\$#\1 s3://${BUCKET}/${KEY}#" "${BUILT_TEMPLATE}"
rm -f "${BUILT_TEMPLATE}.bak"

echo "MicroVM code uploaded to s3://${BUCKET}/${KEY}"
echo "Now run: sam deploy"
