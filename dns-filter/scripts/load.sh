#!/bin/bash
set -ex

# This script will load domain names from github.com/hagezi/dns-blocklists into the DDB table
# used by the dns-filter example. This will create ~65 thousand entries so be aware of the cost.

if [ -z "$DNS_TABLE" ]; then
    echo "Error: DNS_TABLE environment variable is not set" >&2
    exit 1
fi

BATCH_SIZE=25  # DynamoDB batch write limit is 25 items
BLOCKLIST_URL="https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/pro.mini-onlydomains.txt"

# Download the blocklist
wget -q -O blocklist.txt "$BLOCKLIST_URL"

# Process the blocklist in batches
BATCH_COUNT=0
BATCH_FILE="batch.json"

# Initialize first batch
echo -n '{"'"$DNS_TABLE"'": [' > "$BATCH_FILE"

# Process each domain
while IFS= read -r domain || [ -n "$domain" ]; do
    # Skip empty lines and comments
    if [ -z "$domain" ] || [[ "$domain" == \#* ]]; then
        continue
    fi
    
    # Add comma if not the first item in the batch
    if [ $BATCH_COUNT -gt 0 ]; then
        echo -n "," >> "$BATCH_FILE"
    fi
    
    # Create the JSON item for this domain
    echo -n '{
        "PutRequest": {
            "Item": {
                "PK": {"S": "'"$domain"'"},
                "SK": {"S": "'"$domain"'"},
                "blocked": {"BOOL": true}
            }
        }
    }' >> "$BATCH_FILE"
    
    BATCH_COUNT=$((BATCH_COUNT + 1))
    
    # If we've reached the batch size limit, finalize this batch and send it
    if [ $BATCH_COUNT -eq $BATCH_SIZE ]; then
        # Finalize the current batch
        echo -n ']}' >> "$BATCH_FILE"
        
        # Send this batch to DynamoDB
        aws dynamodb batch-write-item --request-items file://"$BATCH_FILE"
        
        # Reset for a new batch
        BATCH_COUNT=0
        echo -n '{"'"$DNS_TABLE"'": [' > "$BATCH_FILE"
    fi
done < "blocklist.txt"

# Send the final batch if it has any items
if [ $BATCH_COUNT -gt 0 ]; then
    echo -n ']}' >> "$BATCH_FILE"
    aws dynamodb batch-write-item --request-items file://"$BATCH_FILE"
fi

# Clean up
rm -f blocklist.txt "$BATCH_FILE"
echo "Completed loading domains into DynamoDB table $DNS_TABLE"