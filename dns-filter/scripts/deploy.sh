#!/bin/bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# This script deploys the dns-filter example to multiple regions in a simple
# and demonstrative way. YMMV. Edit the environment variables in the configuration
# script (configure.sh) before running this one.

#
# C O N F I G U R A T I O N
#
. ./scripts/configure.sh
bash ./scripts/publish.sh
HOME_BUCKET="${DEPLOY_BUCKET_NAME_PREFIX}${AWS_REGION}"
ARTIFACT_KMS_KEY_ARN=$(aws s3api get-bucket-encryption --bucket "$HOME_BUCKET" \
  --query 'ServerSideEncryptionConfiguration.Rules[0].ApplyServerSideEncryptionByDefault.KMSMasterKeyID' --output text)
[[ "$ARTIFACT_KMS_KEY_ARN" == arn:*:kms:* ]] || { echo "Artifact bucket must use a customer-managed KMS key ARN." >&2; exit 1; }
[[ "$(aws s3api get-bucket-versioning --bucket "$HOME_BUCKET" --query Status --output text)" == Enabled ]] || {
  echo "Artifact bucket versioning must be enabled." >&2; exit 1;
}

#
# D E P L O Y    G L O B A L    S T A C K
#

# The first deploy is the global stack. This stack creates the the listener and 
# connects it to the regional destinations using a specific name (`stream-starter`).

aws cloudformation deploy \
    --no-fail-on-empty-changeset \
    --template-file ./templates/dns-filter-global.template.json \
    --stack-name ${STACK_NAME}-global \
    --s3-bucket ${DEPLOY_BUCKET_NAME_PREFIX}${AWS_REGION} \
    --s3-prefix "${DEPLOY_BUCKET_PATH_PREFIX}/${STACK_NAME}" \
    --capabilities CAPABILITY_IAM \
    --parameter-overrides \
        ClientCidrToAllow="${ALLOWED_IPS}" \
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
    --parallel \
    --template-file ./templates/dns-filter-region.template.json

TABLE_NAME=$(jq -r .TableName global-stack-outputs.json)
WORKER_HASH=$(sha256sum .build/worker.tar.gz | cut -d ' ' -f 1)
WORKER_KEY="${DEPLOY_BUCKET_PATH_PREFIX}/${STACK_NAME}/worker/${WORKER_HASH}.tar.gz"
WORKER_VERSION=$(aws s3api put-object --bucket "$HOME_BUCKET" --key "$WORKER_KEY" --body .build/worker.tar.gz \
  --server-side-encryption aws:kms --ssekms-key-id "$ARTIFACT_KMS_KEY_ARN" --query VersionId --output text)
aws cloudformation deploy --no-fail-on-empty-changeset --template-file templates/dns-filter-sync.template.json \
  --stack-name "${STACK_NAME}-sync" --capabilities CAPABILITY_IAM --region "$AWS_REGION" \
  --parameter-overrides TableName="$TABLE_NAME" WorkerArtifactBucket="$HOME_BUCKET" \
    WorkerArtifactKey="$WORKER_KEY" WorkerArtifactVersion="$WORKER_VERSION" WorkerArtifactSha256="$WORKER_HASH" \
    ArtifactKeyArn="$ARTIFACT_KMS_KEY_ARN" BlocklistProfile="$BLOCKLIST_PROFILE" \
    SyncSchedule="$SYNC_SCHEDULE" SyncEnabled="$SYNC_ENABLED" WorkerMode="$SYNC_WORKER_MODE" OperationalLogRetentionDays="$OPERATIONAL_LOG_RETENTION"
BUILD_PROJECT=$(aws cloudformation describe-stacks --stack-name "${STACK_NAME}-sync" --region "$AWS_REGION" \
  --query 'Stacks[0].Outputs[?OutputKey==`BuildProject`].OutputValue | [0]' --output text)

ADMIN_URL=""
if [[ "$ENABLE_ADMIN_UI" == true ]]; then
  ADMIN_HASH=$(sha256sum .build/admin.zip | cut -d ' ' -f 1)
  ADMIN_KEY="${DEPLOY_BUCKET_PATH_PREFIX}/${STACK_NAME}/admin/${ADMIN_HASH}.zip"
  ADMIN_VERSION=$(aws s3api put-object --bucket "$HOME_BUCKET" --key "$ADMIN_KEY" --body .build/admin.zip \
    --server-side-encryption aws:kms --ssekms-key-id "$ARTIFACT_KMS_KEY_ARN" --query VersionId --output text)
  ADMIN_PARAMETERS=(TableName="$TABLE_NAME" BuildProject="$BUILD_PROJECT" BlocklistProfile="$BLOCKLIST_PROFILE"
    ArtifactBucket="$HOME_BUCKET" ArtifactKey="$ADMIN_KEY" ArtifactVersion="$ADMIN_VERSION"
    ArtifactKeyArn="$ARTIFACT_KMS_KEY_ARN" OperationalLogRetentionDays="$OPERATIONAL_LOG_RETENTION")
  aws cloudformation deploy --no-fail-on-empty-changeset --template-file templates/dns-filter-admin.template.json \
    --stack-name "${STACK_NAME}-admin" --capabilities CAPABILITY_IAM --region "$AWS_REGION" \
    --parameter-overrides "${ADMIN_PARAMETERS[@]}"
  ADMIN_URL=$(aws cloudformation describe-stacks --stack-name "${STACK_NAME}-admin" --region "$AWS_REGION" \
    --query 'Stacks[0].Outputs[?OutputKey==`AdminUrl`].OutputValue | [0]' --output text)
  aws cloudformation deploy --no-fail-on-empty-changeset --template-file templates/dns-filter-admin.template.json \
    --stack-name "${STACK_NAME}-admin" --capabilities CAPABILITY_IAM --region "$AWS_REGION" \
    --parameter-overrides "${ADMIN_PARAMETERS[@]}" PublicUrl="$ADMIN_URL"
  echo "Admin UI: $ADMIN_URL (invite users to the Cognito admin/viewer groups)"
fi

# Deploy regional analysis/notifications after the UI origin is available.
for DEPLOY_REGION in ${DEPLOY_TO_REGIONS}; do
  DEPLOY_BUCKET="${DEPLOY_BUCKET_NAME_PREFIX}${DEPLOY_REGION}"
  PARAMETER_OVERRIDES=(ClientCidrToAllow="$ALLOWED_IPS"
    OperationalLogRetentionDays="$OPERATIONAL_LOG_RETENTION"
    SuspiciousDomainEmail="$SUSPICIOUS_DOMAIN_EMAIL" AdminUrl="${ADMIN_URL%/}")
  if [[ -n "${DOMAIN_NAME:-}" ]]; then
    PARAMETER_OVERRIDES+=(DomainName="$DOMAIN_NAME" HostedZoneId="$HOSTED_ZONE_ID")
  fi
  if [[ -n "${DNS_LOG_RETENTION:-}" ]]; then
    PARAMETER_OVERRIDES+=(DnsLogRetentionDays="$DNS_LOG_RETENTION")
  fi
  sam deploy \
    --stack-name "$STACK_NAME" \
    --s3-bucket "$DEPLOY_BUCKET" \
    --s3-prefix "${DEPLOY_BUCKET_PATH_PREFIX}/${STACK_NAME}" \
    --capabilities CAPABILITY_IAM \
    --no-fail-on-empty-changeset \
    --region "$DEPLOY_REGION" \
    --parameter-overrides "${PARAMETER_OVERRIDES[@]}"
done
if [[ "$SYNC_ENABLED" == true ]]; then
  if BUILD_RESULT=$(aws codebuild start-build --project-name "$BUILD_PROJECT" --region "$AWS_REGION" 2>&1); then
    printf '%s\n' "$BUILD_RESULT"
  else
    BUILD_STATUS=$?
    printf '%s\n' "$BUILD_RESULT" >&2
    if [[ "$BUILD_RESULT" == *"(AccountLimitExceededException)"* && "$BUILD_RESULT" == *"Concurrent build limit exceeded"* ]]; then
      echo "Stacks deployed successfully, but the post-deploy sync was not started because the build concurrency limit was reached. Inspect existing builds and request another sync after they finish if needed." >&2
    else
      exit "$BUILD_STATUS"
    fi
  fi
fi
