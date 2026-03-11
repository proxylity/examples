#!/usr/bin/env bash
# scripts/2-deploy-aws.sh
#
# Deploys the AWS side of the supabase-udp example:
#   1. Stores the JWT signing secret in SSM Parameter Store (SecureString)
#   2. Builds and deploys the SAM stack (API Gateway, Step Functions, JwtSigner Lambda,
#      Proxylity Listener and Destination, IAM roles)
#
# Prerequisites:
#   - AWS CLI configured with sufficient permissions
#   - SAM CLI installed: pip install aws-sam-cli
#   - scripts/1-deploy-supabase.sh has been run and these env vars are set:
#       JWT_SECRET                  — from step 1 output
#       SUPABASE_EDGE_FUNCTION_URL  — from step 1 output
#
# The SAM deploy runs --guided automatically the first time (no samconfig.toml).
# Subsequent runs skip --guided and use the saved samconfig.toml.

set -euo pipefail
cd "$(dirname "$0")/.."

# Validate required environment variables
if [[ -z "${JWT_SECRET:-}" ]]; then
  echo "ERROR: JWT_SECRET is not set."
  echo "Run scripts/1-deploy-supabase.sh first and export the printed values."
  exit 1
fi
if [[ -z "${SUPABASE_EDGE_FUNCTION_URL:-}" ]]; then
  echo "ERROR: SUPABASE_EDGE_FUNCTION_URL is not set."
  echo "Run scripts/1-deploy-supabase.sh first and export the printed values."
  exit 1
fi

STACK_NAME="${STACK_NAME:-supabase-udp}"
REGION="${AWS_REGION:-us-west-2}"
SSM_PARAM="/supabase-udp/jwt-secret"

echo "==> Storing JWT secret in SSM Parameter Store (SecureString)..."
echo "    Parameter: ${SSM_PARAM}"
aws ssm put-parameter \
  --name  "$SSM_PARAM" \
  --value "$JWT_SECRET" \
  --type  SecureString \
  --overwrite \
  --region "$REGION"
echo "    Done."

echo "==> Building SAM application..."
sam build

echo "==> Deploying SAM stack: ${STACK_NAME} (region: ${REGION})"
GUIDED_FLAG=""
if [[ ! -f samconfig.toml ]]; then
  echo "    samconfig.toml not found — running guided deploy."
  echo "    Accept the default JwtSecretParam (/supabase-udp/jwt-secret)."
  GUIDED_FLAG="--guided"
fi
echo ""
sam deploy \
  --stack-name "$STACK_NAME" \
  --region     "$REGION" \
  --capabilities CAPABILITY_IAM CAPABILITY_NAMED_IAM \
  --parameter-overrides \
    "SupabaseEdgeFunctionUrl=${SUPABASE_EDGE_FUNCTION_URL}" \
    "JwtSecretParam=${SSM_PARAM}" \
  $GUIDED_FLAG

echo ""
echo "==> Fetching stack outputs..."
aws cloudformation describe-stacks \
  --stack-name "$STACK_NAME" \
  --region     "$REGION" \
  --query      "Stacks[0].Outputs" \
  --output     json | tee outputs.json

echo ""
echo "======================================================================"
echo "  AWS deployment complete."
echo ""
echo "  Run scripts/3-start-token-refresh.sh to start the JWT refresh loop."
echo "  Do not send UDP traffic until the loop is running."
echo "======================================================================"
