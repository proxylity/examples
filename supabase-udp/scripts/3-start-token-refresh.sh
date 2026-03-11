#!/usr/bin/env bash
# scripts/3-start-token-refresh.sh
#
# Starts the Step Functions JWT refresh loop.
#
# This must be run ONCE after the AWS stack is deployed and must be running
# before sending any UDP traffic. The execution runs indefinitely:
#   SignJwt → UpdateStageVariable → WaitForExpiry → (repeat)
#
# The state machine writes a new JWT to the API Gateway SUPABASE_JWT stage
# variable before each token expires. The Edge Function validates this JWT on
# every request; without it, all packets will receive no response.
#
# To stop the loop: aws stepfunctions stop-execution --execution-arn <arn>
# To check status:  aws stepfunctions list-executions --state-machine-arn <arn>

set -euo pipefail
cd "$(dirname "$0")/.."

STACK_NAME="${STACK_NAME:-supabase-udp}"
REGION="${AWS_REGION:-us-west-2}"
OUTPUTS_FILE="outputs.json"

if [[ ! -f "$OUTPUTS_FILE" ]]; then
  echo "==> outputs.json not found. Fetching stack outputs..."
  aws cloudformation describe-stacks \
    --stack-name "$STACK_NAME" \
    --region     "$REGION" \
    --query      "Stacks[0].Outputs" \
    --output     json > "$OUTPUTS_FILE"
fi

get_output() {
  python3 -c "
import sys, json
outputs = json.load(open('${OUTPUTS_FILE}'))
val = next((o['OutputValue'] for o in outputs if o['OutputKey'] == '$1'), None)
if not val:
    sys.exit(1)
print(val)
"
}

SFN_ARN=$(get_output JwtRefreshStateMachineArn)
API_ID=$(get_output SupabaseApiId)
ENDPOINT=$(get_output Endpoint)

echo "==> Starting JWT refresh loop..."
echo "    State machine: ${SFN_ARN}"
echo "    API Gateway:   ${API_ID}"
echo ""

EXECUTION_ARN=$(aws stepfunctions start-execution \
  --state-machine-arn "$SFN_ARN" \
  --input             "{\"restApiId\": \"${API_ID}\"}" \
  --region            "$REGION" \
  --query             "executionArn" \
  --output            text)

echo "    Execution ARN: ${EXECUTION_ARN}"
echo ""
echo "==> Waiting for first token to be written (~5 seconds)..."
sleep 6

STATUS=$(aws stepfunctions describe-execution \
  --execution-arn "$EXECUTION_ARN" \
  --region        "$REGION" \
  --query         "status" \
  --output        text)

if [[ "$STATUS" != "RUNNING" ]]; then
  echo "ERROR: Execution is not running (status: ${STATUS})."
  echo "Check the Step Functions console for error details."
  exit 1
fi

echo "======================================================================"
echo "  JWT refresh loop is running."
echo ""
echo "  Send a test packet:"
echo "    echo 'hello' | nc -u ${ENDPOINT%:*} ${ENDPOINT##*:} -w2"
echo ""
echo "  Verify data arrived in Supabase:"
echo "    Open the Supabase dashboard → Table Editor → udp_messages"
echo "    Or: SQL Editor → run: SELECT * FROM udp_messages ORDER BY received_at DESC LIMIT 5"
echo ""
echo "  Stop the loop:"
echo "    aws stepfunctions stop-execution --execution-arn ${EXECUTION_ARN} --region ${REGION}"
echo "======================================================================"
