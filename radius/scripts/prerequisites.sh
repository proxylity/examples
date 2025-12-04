#!/bin/bash

# Check for AWS CLI installation
if ! command -v aws &> /dev/null; then
    echo "AWS CLI not found. Please install and configure it before running this script."
    return
fi
echo "AWS CLI found."

# if DEPLOY_BUCKET_NAME_PREFIX is already set, give the user the option to keept it or clear it and proceed
if [[ -n "$DEPLOY_BUCKET_NAME_PREFIX" ]]; then
    read -p "Existing DEPLOY_BUCKET_NAME_PREFIX found: $DEPLOY_BUCKET_NAME_PREFIX. Do you want to keep it? (y/n): " keep_prefix
    if [[ "$keep_prefix" != "y" ]]; then
        unset DEPLOY_BUCKET_NAME_PREFIX
    else
        echo "Using existing DEPLOY_BUCKET_NAME_PREFIX: $DEPLOY_BUCKET_NAME_PREFIX"
        return
    fi
fi

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
            ((PREFIX_COUNT["$prefix"]++))
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

        if [[ "$region" == "us-east-1" ]]; then
            aws s3api create-bucket \
                --bucket "$bucket" \
                --region "$region" \
                2>/dev/null && echo "✔ Created $bucket" || echo "⚠️  Could not create $bucket (may already exist)"
        else
            aws s3api create-bucket \
                --bucket "$bucket" \
                --region "$region" \
                --create-bucket-configuration LocationConstraint="$region" \
                2>/dev/null && echo "✔ Created $bucket" || echo "⚠️  Could not create $bucket (may already exist)"
        fi
    done

    export DEPLOY_BUCKET_NAME_PREFIX="$prefix"
fi

echo "$DEPLOY_BUCKET_NAME_PREFIX"
