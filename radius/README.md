# RADIUS Authorization and Accounting with UDP Gateway

This example demonstrates a cloud-based RADIUS authorization and accounting system using Proxylity's UDP Gateway service. The current implementation provides a foundation with basic packet processing and can be extended to include comprehensive enterprise-class security features.

## Architecture Overview

The system uses two UDP Gateway listeners for RADIUS authentication and accounting, separating the types of packets by port (as is traditional with RADIUS).  Each listener is configured to deliver inbound packets to two destinations: one to generate a response, and the other to archive and process the requests.

The authentication flow handles RADIUS authentication requests using step functions and Kinesis Firehose.  Authenticates requests against DynamoDB records and processes request archives with Bedrock LLM.

The accounting flow handles RADIUS accounting requests using Lambda and Kinesis Firehose. It processes those requests to update per-session packet count and traffic volume aggregates in S3.

<img src="architecture.png" title="RADIUS on UDP Gateway Architecture" width=600 />

## Current Implementation Status

⚠️ **Note**: This is a foundational implementation that provides:
- Basic UDP packet reception, responses and logging via Proxylity UDP Gateway
- Infrastructure framework for RADIUS processing
- Secure data storage and encryption setup
- Multi-region deployment capability

## Components

### Global Resources (`global.template.json`)
- UDP Gateway listeners for authentication and accounting
- CloudWatch Log Group for destination delivery logging (30-day retention)
- IAM roles for Lambda functions and Proxylity service
- Cross-region permissions for multi-region deployment

### Regional Resources (`region.template.json`)

The regional deployment uses a nested template approach. It creates a CloudWatch dashboard for observability, and orchestrates the creation of nested stacks:

**Shared Infrastructure:** (`region-shared.template.json`)
- KMS key and Alias for encryption
- RADIUS packet parser and writer Lambda functions

**Nested Authentication Stack (`region-auth.template.json`):**
- Authentication Lambda function
- DynamoDB table for session state tracking
- CloudWatch log groups and dead letter queues
- IAM policies for authentication services

**Nested Accounting Stack (`region-acct.template.json`):**
- Accounting Lambda function
- CloudWatch log groups and dead letter queues
- IAM policies for accounting services

## Security Features

### Encryption
- **KMS encryption** for all data at rest (DynamoDB and CloudWatch Logs)
- **Customer-managed keys** for enhanced control
- **Automatic key rotation** can be enabled

### Access Control
- **Least privilege IAM policies** with specific resource ARNs

## Deployment

### Prerequisites
1. Active subscription to Proxylity UDP Gateway in AWS Marketplace
2. AWS CLI (`aws`) and SAM CLI (`sam`) configured with appropriate permissions
3. The .Net SDK version 8 installed
4. The `make` and `jq` tools installed

### Using Automated Scripts (Recommended)
The repository includes deployment scripts for streamlined deployment accross multiple regions:

```bash
# Choose or create deployment buckets (note the extra . to preseve envars)
. ./scripts/prerequisites.sh

# Run deployment script
AWS_REGION=us-west-2 ./scripts/deploy.sh
```

### Manual Deployment
For manual deployment or to understand the deployment process in detail, follow these steps:

#### Step 0: Run Checks (Optional)
If your environment has `cfn-lint` installed, you can run `checks.sh` to verify the CloudFormation templates are valid:

```bash
./scripts/checks.sh
```

#### Step 1: Deploy Global Stack
Deploy the global stack that creates the UDP Gateway listeners and IAM roles:

```bash
aws cloudformation deploy \
  --template-file templates/global.template.json \
  --stack-name radius-global \
  --capabilities CAPABILITY_NAMED_IAM \
  --parameter-overrides \
    ClientCidrToAllow="$(curl -s checkip.amazonaws.com)/32"
```

**NOTE:** The snippet above restricts access to the listeners from *only* your current public IP address. If you want to allow access from other sources, update the value of `ClientCidrToAllow` to specify additional CIDR blocks.

Next, capture the outputs of the global stack and transform them into JSON format for consumption by regional stacks using `FnTransform` with `AWS::Include` in the regional stack's `Mappings`, and `Fn::FindInMap` to extract the values needed.

```bash
aws cloudformation describe-stacks \
    --stack-name radius-global \
    --query "Stacks[0]" \
    --output json \
    > radius-global.outputs

jq "[.Outputs[]|{(.OutputKey):.OutputValue}]|add" radius-global.outputs > global-stack-outputs.json
```

#### Step 2: Build and Deploy Regional Stacks
Build the .NET Lambda functions and deploy using SAM:

```bash
# Build the application
sam build --template-file templates/region.template.json

# Deploy to target region (e.g., us-west-2)
sam deploy \
  --stack-name radius-region \
  --s3-bucket <your-deployment-bucket> \
  --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
  --parameter-overrides \
    RadiusLogRetentionDays=90 \
    LambdaLogLevel="INFO"
```

## Configuration Parameters

### Global Template Parameters
- **ClientCidrToAllow**: CIDR block for allowed client IP addresses (default: 0.0.0.0/0)

### Regional Template Parameters
- **RadiusLogRetentionDays**: Log retention period in days (default: 89)
- **AuthStateTableReadCapacity**: DynamoDB read capacity (default: 0 = on-demand)
- **AuthStateTableWriteCapacity**: DynamoDB write capacity (default: 0 = on-demand)
- **LambdaLogLevel**: Log level for Lambda functions (default: INFO)

Note: The regional template retrieves global stack outputs from the JSON file created in Step 1 using the `jq` command above. 

## Monitoring and Observability

### CloudWatch Dashboard
Each regional deployment creates a CloudWatch dashboard with:
- Lambda function metrics (invocations, errors, duration)
- DynamoDB capacity utilization

### Metrics and Alarms
- **Lambda errors**: Automatically tracked with CloudWatch metrics
- **DynamoDB throttling**: Monitor through capacity utilization metrics
- **Dead letter queues**: Monitor message counts for error detection
- **UDP Gateway destinations**: Custom CloudWatch metrics enabled to track packet traffic and error counts

### Current Logging
- **Packet Logging**: Lambda functions log received packet details
- **CloudWatch Logs**: Centralized logging for Lambda and Step Functions executions, and Destination delivery issues

## Current Data Storage

### Authentication State (DynamoDB)
- **Table**: Created by nested authentication stack
- **Encryption**: Customer-managed KMS key
- **Capacity**: Configurable (on-demand by default)

### Request and Log Storage (CloudWatch Logs and S3)
- **Encryption**: Customer-managed KMS key
- **Logs**: Lambda and StepFunctions execution logs are stored in CloudWatch Logs with configurable retention
- **S3**: RADIUS Requests are stored in batches in S3. Parsed content of accounting packets and calculated aggregates as well.

## Scaling Considerations

### Lambda Concurrency
- Default concurrency limits may apply
- Consider reserved concurrency for predictable workloads
- Monitor throttling metrics for performance optimization

### DynamoDB Capacity
- Default configuration uses on-demand capacity for flexible scaling
- Consider provisioned capacity for predictable workloads
- Monitor capacity utilization metrics

### UDP Gateway Limits
- Free plan limits may apply
- Monitor UDP Gateway destination metrics in CloudWatch for packet traffic and error analysis

## Troubleshooting

### Common Issues
1. **Permission Errors**: Verify IAM roles and cross-stack references in global-outputs.json
2. **Lambda Timeouts**: Check function timeout settings and CloudWatch logs
3. **DynamoDB Throttling**: Monitor capacity utilization (on-demand scaling should handle most cases)
4. **Nested Stack Deployment**: Ensure proper SAM CLI setup and S3 bucket permissions
5. **Destination Delivery Failures**: Check the destination logs in CloudWatch for delivery errors

### Debug Resources
- **CloudWatch Logs**: Review function execution logs and packet processing details
- **Destination Logs**: Review UDP Gateway destination delivery logs for troubleshooting
- **CloudWatch Dashboard**: Monitor Lambda and DynamoDB metrics

## Development and Extension

### Building Lambda Functions

**Using SAM (Recommended):**
```bash
# Build all functions using SAM
sam build --template-file templates/region.template.json
```

**Using Make (Individual Functions):**
```bash
# Build authentication Lambda
cd src/radius-auth-lambda
make build-Lambda

# Build accounting Lambda  
cd src/radius-acct-lambda
make build-Lambda
```

### Extending the Implementation
To implement full RADIUS processing:

1. **Add RADIUS Protocol Parsing**: Implement additional RADIUS packet structure parsing needed, if any (e.g. VSAs)
2. **Add Authentication Logic**: Integrate with authentication databases
3. **Add Response Generation**: Generate appropriate RADIUS responses
4. **Add Session Management**: Implement a session state tracking in DynamoDB

## Template Architecture

### Nested Template Structure
```
region.template.json (Parent)
├── region-shared.template.json (Shared Stack)
├── region-auth.template.json (Authentication Stack)
├── region-acct.template.json (Accounting Stack)
└── Shared Dashboard
```

### Template Dependencies
- Parent template creates shared dashboard (depends on nested stack outputs)
- Nested templates reference parent resources via parameters
- Global stack outputs accessed via included JSON mapping

## Security Best Practices

### Network Security
- Restrict ClientCidrToAllow to known networks in global template
- Monitor network access patterns via CloudWatch metrics

### Data Protection
- KMS keys are customer-managed with configurable access policies
- Audit access patterns with CloudTrail (customer responsibility)
- Implement data classification policies as needed

## Testing Authentication

### DynamoDB Record Schema

The authentication state machine uses a DynamoDB table with a single-table design. The table uses `PK` (partition key) and `SK` (sort key) with patterns to support different record types.

#### Record Types

| Record Type | PK Pattern | SK Pattern | Description |
|-------------|------------|------------|-------------|
| User | `USER#<username>` | `#CONFIG` | User credentials and configuration |
| NAS | `NAS#<nas_identifier>` | `#CONFIG` | Network Access Server configuration |
| Session | `SESSION#<session_id>` | `<end_timestamp>` | Authentication session records (auto-created) |

#### User Record Attributes

| Attribute | Type | Description |
|-----------|------|-------------|
| `PK` | String | `USER#<username>` - Username or MAC address (lowercase, no separators for MAC) |
| `SK` | String | `#CONFIG` - Fixed value for user configuration records |
| `user_password` | String | Password for PAP auth, or MAC address for MAC auth |
| `vlan` | String | (Optional) VLAN to assign to authenticated user |
| `groups` | String Set | (Optional) Group memberships returned as RADIUS Class attribute |
| `is_mac_auth` | Boolean | (Optional) Indicates if this is a MAC auth record |
| `TTL` | Number | (Optional) Unix timestamp for record expiration |

#### NAS Record Attributes

| Attribute | Type | Description |
|-----------|------|-------------|
| `PK` | String | `NAS#<nas_identifier>` - NAS-Identifier from RADIUS request |
| `SK` | String | `#CONFIG` - Fixed value for NAS configuration records |
| `session_duration` | Number | Session timeout in seconds (default: 3600) |
| `vlan` | String | (Optional) Default VLAN for users authenticating via this NAS |
| `auto_allow_users` | String Set | (Optional) Usernames or `*` to auto-accept without password verification |

### Creating Test Records

First, get the DynamoDB table name from your deployed stack:

```bash
# Get the table name from the regional stack outputs
TABLE_NAME=$(aws cloudformation describe-stacks \
  --stack-name radius-region \
  --query "Stacks[0].Outputs[?OutputKey=='RadiusAuthStateTableName'].OutputValue" \
  --output text)

echo "Table name: $TABLE_NAME"
```

#### Create a PAP/CHAP User Record

Create a user that authenticates with username/password (PAP):

```bash
aws dynamodb put-item \
  --table-name "$TABLE_NAME" \
  --item '{
    "PK": {"S": "USER#testuser"},
    "SK": {"S": "#CONFIG"},
    "user_password": {"S": "testpassword"},
    "vlan": {"S": "100"},
    "groups": {"SS": ["employees", "wifi-users"]}
  }'
```

#### Create a MAC Auth Bypass (MAB) Record

Create a MAC address record for devices that authenticate using their MAC address:

```bash
aws dynamodb put-item \
  --table-name "$TABLE_NAME" \
  --item '{
    "PK": {"S": "USER#aabbccddeeff"},
    "SK": {"S": "#CONFIG"},
    "user_password": {"S": "aabbccddeeff"},
    "is_mac_auth": {"BOOL": true},
    "vlan": {"S": "200"},
    "groups": {"SS": ["iot-devices"]}
  }'
```

**Note:** MAC addresses must be lowercase with no separators (colons, hyphens, or spaces).

#### Create a NAS Configuration Record

Configure a NAS device with custom session duration and auto-allow rules:

```bash
aws dynamodb put-item \
  --table-name "$TABLE_NAME" \
  --item '{
    "PK": {"S": "NAS#my-access-point"},
    "SK": {"S": "#CONFIG"},
    "session_duration": {"N": "7200"},
    "vlan": {"S": "50"},
    "auto_allow_users": {"SS": ["guest", "admin"]}
  }'
```

#### Create a NAS with Wildcard Auto-Allow

Configure a NAS that accepts all users without password verification:

```bash
aws dynamodb put-item \
  --table-name "$TABLE_NAME" \
  --item '{
    "PK": {"S": "NAS#open-access-point"},
    "SK": {"S": "#CONFIG"},
    "session_duration": {"N": "1800"},
    "auto_allow_users": {"SS": ["*"]}
  }'
```

### Verifying Records

List all user records in the table:

```bash
aws dynamodb scan \
  --table-name "$TABLE_NAME" \
  --filter-expression "begins_with(PK, :pk)" \
  --expression-attribute-values '{":pk": {"S": "USER#"}}' \
  --query "Items[*].{PK: PK.S, Password: user_password.S, VLAN: vlan.S}"
```

List all NAS records:

```bash
aws dynamodb scan \
  --table-name "$TABLE_NAME" \
  --filter-expression "begins_with(PK, :pk)" \
  --expression-attribute-values '{":pk": {"S": "NAS#"}}' \
  --query "Items[*].{PK: PK.S, SessionDuration: session_duration.N, AutoAllow: auto_allow_users.SS}"
```

### Deleting Test Records

```bash
# Delete a user record
aws dynamodb delete-item \
  --table-name "$TABLE_NAME" \
  --key '{"PK": {"S": "USER#testuser"}, "SK": {"S": "#CONFIG"}}'

# Delete a NAS record
aws dynamodb delete-item \
  --table-name "$TABLE_NAME" \
  --key '{"PK": {"S": "NAS#my-access-point"}, "SK": {"S": "#CONFIG"}}'
```

### Support and Enhancement
For questions about extending this implementation, please reach out to [Proxylity Support](mailto:support@proxylity.com).
