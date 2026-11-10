#!/bin/bash
set -euo pipefail
TEMPLATE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../templates" && pwd)/dns-filter-artifacts.template.json"

DEPLOY_TO_REGIONS="us-west-2 us-east-1 eu-west-1"
read -ra REGIONS <<< "$DEPLOY_TO_REGIONS"

echo "Checking for existing buckets in regions: ${REGIONS[*]}..."

# Gather existing matching bucket name prefixes
declare -A PREFIX_COUNT
echo "Retrieving list of all S3 buckets..."
buckets=$(aws s3api list-buckets --query "Buckets[].Name" --output text)

for region in "${REGIONS[@]}"; do
    for bucket in $buckets; do
        if [[ "$bucket" == *-$region ]]; then
            prefix="${bucket%-${region}}"
            PREFIX_COUNT["$prefix"]=$((${PREFIX_COUNT["$prefix"]:-0} + 1))
        fi
    done
done

# Filter to valid sets
VALID_PREFIXES=()
for prefix in "${!PREFIX_COUNT[@]}"; do
    if [[ "${PREFIX_COUNT[$prefix]}" -eq "${#REGIONS[@]}" ]]; then
        VALID_PREFIXES+=("$prefix-")
    fi
done

# User selects or creates
echo
echo "Found ${#VALID_PREFIXES[@]} existing bucket set(s):"
for i in "${!VALID_PREFIXES[@]}"; do
    echo "$((i + 1)). ${VALID_PREFIXES[$i]}"
done
echo "$(( ${#VALID_PREFIXES[@]} + 1 )). Create new bucket set"

read -p "Select an option [1-$(( ${#VALID_PREFIXES[@]} + 1 ))]: " selection

echo

if [[ "$selection" -ge 1 && "$selection" -le "${#VALID_PREFIXES[@]}" ]]; then
    export DEPLOY_BUCKET_NAME_PREFIX="${VALID_PREFIXES[$((selection - 1))]}"
else
    read -p "Enter new bucket name prefix: " prefix
    [[ "$prefix" != *- ]] && prefix="$prefix-"

    for region in "${REGIONS[@]}"; do
        bucket="${prefix}${region}"
        echo "Creating bucket: $bucket in region: $region"

        aws cloudformation deploy --no-fail-on-empty-changeset --template-file "$TEMPLATE" \
            --stack-name "dns-artifacts-${region}" --region "$region" \
            --parameter-overrides BucketName="$bucket" \
              AdminFunctionName="${STACK_NAME:-dns-filter}-admin" \
              AdminArtifactPrefix="${DEPLOY_BUCKET_PATH_PREFIX:-builds}/${STACK_NAME:-dns-filter}/admin/"
    done

    export DEPLOY_BUCKET_NAME_PREFIX="$prefix"
fi
for region in "${REGIONS[@]}"; do
    bucket="${DEPLOY_BUCKET_NAME_PREFIX}${region}"
    encryption=$(aws s3api get-bucket-encryption --bucket "$bucket" \
      --query 'ServerSideEncryptionConfiguration.Rules[0].ApplyServerSideEncryptionByDefault.KMSMasterKeyID' --output text)
    [[ "$encryption" == arn:*:kms:* ]] || { echo "$bucket needs a customer-managed SSE-KMS key ARN." >&2; exit 1; }
    [[ "$(aws s3api get-bucket-versioning --bucket "$bucket" --query Status --output text)" == Enabled ]] || {
      echo "$bucket needs versioning enabled before deployment." >&2; exit 1;
    }
done

echo "$DEPLOY_BUCKET_NAME_PREFIX"
echo "Existing bucket sets are validated, not updated. Ensure their bucket and KMS policies allow the admin Web Function to read its archives." >&2
