#!/usr/bin/env bash
#
# Builds the MicroVM image directly with the CLI (outside CloudFormation) so that a failed build is
# NOT rolled back: the image, its state reason, and its build log all stay around to read.
#
# Creates (and reuses) a debug build role and log group, and a separate image named
# iperf2-microvm-debug. Clean up with: scripts/dev/debug-image-build.sh --cleanup
#
# Usage: scripts/dev/debug-image-build.sh [s3://bucket/key.zip]   (default: newest zip from deploy.sh)
#   ARCH=X86_64|ARM_64 (default X86_64), BASE_IMAGE_VERSION (default 3)
set -euo pipefail
cd "$(dirname "$0")/../.."
export AWS_REGION="${AWS_REGION:-us-west-2}"
ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
NAME=iperf2-microvm-debug
ROLE=iperf2-microvm-debug-build
LOG_GROUP=/aws/lambda-microvms/$NAME
ARN="arn:aws:lambda:${AWS_REGION}:${ACCOUNT}:microvm-image:${NAME}"
ARCH="${ARCH:-X86_64}"
BASE_VERSION="${BASE_IMAGE_VERSION:-3}"

if [[ "${1:-}" == --cleanup ]]; then
  aws lambda-microvms delete-microvm-image --image-identifier "$ARN" 2>/dev/null && echo "deleted image" || true
  aws iam delete-role-policy --role-name "$ROLE" --policy-name build 2>/dev/null || true
  aws iam delete-role --role-name "$ROLE" 2>/dev/null && echo "deleted role" || true
  aws logs delete-log-group --log-group-name "$LOG_GROUP" 2>/dev/null && echo "deleted log group" || true
  exit 0
fi

URI="${1:-}"
if [[ -z "$URI" ]]; then
  # Package build/image/ exactly as deploy.sh does.
  BUCKET=$(aws cloudformation describe-stacks --stack-name aws-sam-cli-managed-default \
    --query "Stacks[0].Outputs[?OutputKey=='SourceBucket'].OutputValue" --output text)
  mkdir -p .aws-sam
  HASH=$(cat build/image/* | sha256sum | cut -c1-16)
  ZIP=".aws-sam/iperf2-microvm-${HASH}.zip"
  rm -f "$ZIP"
  (cd build/image && zip -q -X "../../$ZIP" Dockerfile iperf2-microvm)
  URI="s3://${BUCKET}/iperf2-microvm/image-${HASH}.zip"
  aws s3 cp "$ZIP" "$URI" --only-show-errors
fi
BUCKET=$(cut -d/ -f3 <<<"$URI")
echo "Artifact $URI, arch $ARCH, base version $BASE_VERSION"

if ! aws iam get-role --role-name "$ROLE" >/dev/null 2>&1; then
  aws iam create-role --role-name "$ROLE" --assume-role-policy-document '{
    "Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},
    "Action":["sts:AssumeRole","sts:TagSession"]}]}' >/dev/null
  aws iam put-role-policy --role-name "$ROLE" --policy-name build --policy-document "{
    \"Version\":\"2012-10-17\",\"Statement\":[
      {\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\"],\"Resource\":\"arn:aws:s3:::${BUCKET}/*\"},
      {\"Effect\":\"Allow\",\"Action\":[\"logs:CreateLogGroup\",\"logs:CreateLogStream\",\"logs:PutLogEvents\"],
       \"Resource\":\"arn:aws:logs:${AWS_REGION}:${ACCOUNT}:log-group:${LOG_GROUP}*\"}]}"
  echo "Created build role $ROLE; waiting for IAM to propagate..."
  sleep 15
fi
aws logs create-log-group --log-group-name "$LOG_GROUP" 2>/dev/null || true

COMMON=(--code-artifact "uri=$URI"
  --base-image-arn "arn:aws:lambda:${AWS_REGION}:aws:microvm-image:al2023-1" --base-image-version "$BASE_VERSION"
  --build-role-arn "arn:aws:iam::${ACCOUNT}:role/${ROLE}"
  --cpu-configurations "architecture=$ARCH"
  --logging "{\"cloudWatch\":{\"logGroup\":\"$LOG_GROUP\"}}"
  --hooks '{"port":9000,"microvmImageHooks":{"ready":"ENABLED","readyTimeoutInSeconds":60},"microvmHooks":{"run":"ENABLED","runTimeoutInSeconds":10}}')
if aws lambda-microvms get-microvm-image --image-identifier "$ARN" >/dev/null 2>&1; then
  aws lambda-microvms update-microvm-image --image-identifier "$ARN" "${COMMON[@]}" >/dev/null
else
  aws lambda-microvms create-microvm-image --name "$NAME" "${COMMON[@]}" >/dev/null
fi

echo "Building..."
for _ in $(seq 1 90); do
  STATE=$(aws lambda-microvms get-microvm-image --image-identifier "$ARN" --output json)
  S=$(jq -r '.state // .status // "?"' <<<"$STATE")
  printf '  %s %s\n' "$(date +%T)" "$S"
  [[ $S == CREATED || $S == UPDATED || $S == *FAILED ]] && break
  sleep 10
done
echo "== image"; jq -c 'del(.hooks)' <<<"$STATE"
echo "== versions"
aws lambda-microvms list-microvm-image-versions --image-identifier "$ARN" --output json | jq -c '.items[]?'
echo "== build log"
aws logs filter-log-events --log-group-name "$LOG_GROUP" --start-time $(( ($(date +%s) - 3600) * 1000 )) \
  --query 'events[].message' --output text | tr '\t' '\n' | tail -80
