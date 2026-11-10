#!/usr/bin/env bash
#
# Builds and deploys the example in one step:
#   1. publish the NativeAOT executable into build/image/ (scripts/publish.sh)
#   2. zip build/image/ and upload it to S3 (named by content hash, so unchanged code isn't rebuilt)
#   3. deploy template.yaml with CodeUri pointing at the upload
#
# Usage: scripts/deploy.sh [extra sam deploy --parameter-overrides, e.g. TenantId=iperf2-2]
#   STACK_NAME   default iperf2-microvm
#   AWS_REGION   default us-west-2
#   CLIENT_CIDR  default: your current public IPv4 address /32
#   BUCKET       artifact bucket in the region (default: SAM managed bucket, or one created for you)
#   SKIP_PUBLISH=1  reuse build/image/ as is
#
# Why not "sam build && sam deploy"? At the time of writing, SAM CLI can't upload a local CodeUri for
# AWS::Serverless::MicrovmImage (aws/aws-sam-cli#9283), so this script does that part itself.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib/common.sh

ARCH_PARAM=X86_64
[[ "${RID:-linux-x64}" == linux-arm64 ]] && ARCH_PARAM=ARM_64

if [[ "${SKIP_PUBLISH:-0}" != 1 ]]; then
  WORK_DIR="${WORK_DIR:-$HOME/iperf2-microvm}" bash scripts/publish.sh
fi
[[ -x build/image/iperf2-microvm ]] || { echo "build/image/iperf2-microvm is missing; run scripts/publish.sh" >&2; exit 1; }

ACCOUNT=$(aws sts get-caller-identity --query Account --output text)

# A first deploy that fails leaves the stack in ROLLBACK_COMPLETE: it holds no resources, and
# CloudFormation only allows deleting it. Clear it so the next deploy can start over.
STATUS=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" \
  --query 'Stacks[0].StackStatus' --output text 2>/dev/null || true)
if [[ "$STATUS" == ROLLBACK_COMPLETE ]]; then
  echo "Stack $STACK_NAME is in ROLLBACK_COMPLETE from a failed first deploy; deleting it (it holds no resources)."
  aws cloudformation delete-stack --stack-name "$STACK_NAME"
  aws cloudformation wait stack-delete-complete --stack-name "$STACK_NAME"
fi
CLIENT_CIDR="${CLIENT_CIDR:-$(curl -fs https://checkip.amazonaws.com | tr -d '[:space:]')/32}"
echo "Account $ACCOUNT, region $AWS_REGION, stack $STACK_NAME, allowing $CLIENT_CIDR"

BUCKET="${BUCKET:-}"
if [[ -z "$BUCKET" ]]; then
  BUCKET=$(aws cloudformation describe-stacks --stack-name aws-sam-cli-managed-default \
    --query "Stacks[0].Outputs[?OutputKey=='SourceBucket'].OutputValue" --output text 2>/dev/null || true)
fi
if [[ -z "$BUCKET" || "$BUCKET" == None ]]; then
  BUCKET="iperf2-microvm-artifacts-${ACCOUNT}-${AWS_REGION}"
  if ! aws s3api head-bucket --bucket "$BUCKET" 2>/dev/null; then
    echo "Creating artifact bucket $BUCKET"
    aws s3api create-bucket --bucket "$BUCKET" --create-bucket-configuration LocationConstraint="$AWS_REGION" >/dev/null
    aws s3api put-public-access-block --bucket "$BUCKET" --public-access-block-configuration \
      BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
  fi
fi

mkdir -p .aws-sam
HASH=$(cat build/image/* | sha256sum | cut -c1-16)
ZIP=".aws-sam/iperf2-microvm-${HASH}.zip"
KEY="iperf2-microvm/image-${HASH}.zip"
rm -f "$ZIP"
(cd build/image && zip -q -X "../../$ZIP" Dockerfile iperf2-microvm)
aws s3 cp "$ZIP" "s3://${BUCKET}/${KEY}" --only-show-errors
echo "Uploaded s3://${BUCKET}/${KEY} ($(du -h "$ZIP" | cut -f1))"

# Newest base image version that supports our architecture. The docs describe status and
# supportedCpuConfigurations fields; when the API omits them (as it does at the time of writing), fall
# back to the documented rule: x86_64 needs version 3 or later. BASE_IMAGE_VERSION overrides all this.
BASE_VERSION="${BASE_IMAGE_VERSION:-}"
if [[ -z "$BASE_VERSION" ]]; then
  MIN_VERSION=0; [[ $ARCH_PARAM == X86_64 ]] && MIN_VERSION=3
  BASE_VERSION=$(aws lambda-microvms list-managed-microvm-image-versions \
    --image-identifier "arn:aws:lambda:${AWS_REGION}:aws:microvm-image:al2023-1" --output json |
    jq -r --arg a "$ARCH_PARAM" --argjson min "$MIN_VERSION" '[.items[]
        | select((.status // "AVAILABLE") == "AVAILABLE")
        | select(if .supportedCpuConfigurations then any(.supportedCpuConfigurations[]; .architecture == $a)
                 else (.imageVersion | tonumber) >= $min end)
        | .imageVersion] | sort_by(tonumber) | last // empty')
fi
if [[ -z "$BASE_VERSION" ]]; then
  echo "No al2023-1 base image version supports $ARCH_PARAM in $AWS_REGION." >&2
  echo "Try RID=linux-arm64 (ARM_64), or check: aws lambda-microvms list-managed-microvm-image-versions" >&2
  exit 1
fi
echo "Base image al2023-1 version $BASE_VERSION ($ARCH_PARAM)"

# Keep the current TenantId on redeploys unless one is given (scripts/fresh-vm.sh changes it).
OVERRIDES=("$@")
if [[ " ${OVERRIDES[*]} " != *" TenantId="* ]]; then
  CURRENT_TENANT=$(aws cloudformation describe-stacks --stack-name "$STACK_NAME" \
    --query "Stacks[0].Parameters[?ParameterKey=='TenantId'].ParameterValue" --output text 2>/dev/null || true)
  [[ -n "$CURRENT_TENANT" && "$CURRENT_TENANT" != None ]] && OVERRIDES+=("TenantId=$CURRENT_TENANT")
fi

# The template has exactly one CodeUri (the image), so a line-level replacement is safe.
sed -E "s#^([[:space:]]*CodeUri:).*\$#\1 s3://${BUCKET}/${KEY}#" template.yaml > .aws-sam/packaged.yaml

sam deploy \
  --template-file .aws-sam/packaged.yaml \
  --stack-name "$STACK_NAME" \
  --region "$AWS_REGION" \
  --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
  --no-fail-on-empty-changeset \
  --parameter-overrides ClientCidrToAllow="$CLIENT_CIDR" Architecture="$ARCH_PARAM" \
    BaseImageVersion="$BASE_VERSION" ${OVERRIDES[@]+"${OVERRIDES[@]}"}

load_outputs
echo
echo "Listener: $DOMAIN:$PORT (tenant $TENANT_ID)"
echo "Try:      iperf -u -c $DOMAIN -p $PORT -t 10 -b 1M"
echo "Prove:    scripts/prove/run-all.sh"
