## Enterprise Syslog Collection — WireGuard + CloudWatch + S3 WORM Archive

A production-ready, centralized syslog collection service built on Proxylity UDP Gateway (WireGuard), Amazon Kinesis Firehose, CloudWatch Logs, and S3. Designed for enterprise environments where security, compliance, and operational simplicity matter.

## Architecture

Syslog traffic is encrypted in transit via WireGuard before it ever leaves the source network. The Proxylity listener authenticates each peer by its public key, decapsulates the syslog UDP packets, and fans them out to two destinations: CloudWatch Logs for live querying and alerting, and Kinesis Firehose for durable delivery to S3 for long-term WORM archival.

Each physical site, branch office, or AWS account adds one WireGuard peer entry — the network gateway for that site. The gateway relays syslog from all downstream devices; individual hosts require no per-device configuration. A single deployed stack collects from any number of sites simultaneously.

![Architecture](./architecture-multi-site.svg)

Gateways forward syslog from downstream devices through the WireGuard tunnel, re-sourcing traffic from the gateway's tunnel IP. Downstream device hostnames are preserved in the syslog `HOSTNAME` field (RFC 5424), so log attribution is maintained at the application layer.

For zero-client-reconfiguration migration from an on-premises syslog server, configure policy-based routing (PBR) + DNAT on the gateway to transparently redirect all port-514 UDP traffic into the WireGuard tunnel. Clients continue pointing at any IP on port 514 without changes.

## Security Properties

| Control | Implementation |
|---|---|
| Encryption in transit | WireGuard (ChaCha20-Poly1305, Curve25519) — cryptographic peer authentication |
| Encryption at rest | Customer-managed KMS key (CMK) on both Firehose delivery and S3 storage |
| Key rotation | Automatic KMS key rotation, configurable period (default 365 days) |
| Log immutability | S3 Object Lock in GOVERNANCE mode — objects cannot be deleted or overwritten before the retention period expires |
| S3 access enforcement | Bucket policy denies all non-TLS requests (`aws:SecureTransport`) |
| IAM least privilege | Dedicated roles for Firehose delivery and Proxylity destination; no broad permissions |

> **Object Lock:** GOVERNANCE mode prevents deletion by ordinary principals for the duration of `LogArchiveExpirationInDays`. Principals with `s3:BypassGovernanceRetention` permission (e.g. a designated security admin role) can override this if operationally necessary. For fully immutable compliance storage, change the mode to `COMPLIANCE`, which prevents deletion by any principal including root.

> **KMS key rotation:** When the KMS key rotates, AWS retains all previous backing key versions indefinitely. Existing S3 objects remain fully decryptable — the correct version is selected automatically from metadata embedded in each ciphertext. No re-encryption of existing objects is required.

### Compliance Applicability

This architecture directly addresses controls in common regulatory frameworks:

| Framework | Relevant Controls |
|---|---|
| **SOC 2** | CC6.1 (logical access), CC6.7 (encryption in transit/at rest), CC7.2 (monitoring/alerting) |
| **PCI DSS v4** | Req 10.2–10.5 (audit log protection), Req 10.7 (log availability), Req 4.2.1 (encryption in transit) |
| **HIPAA** | §164.312(b) (audit controls), §164.312(e)(1) (transmission security), §164.312(a)(2)(iv) (encryption) |
| **NIST 800-53** | AU-9 (log protection), AU-11 (retention), SC-8 (transmission confidentiality), SI-12 (information handling) |

Object Lock in GOVERNANCE or COMPLIANCE mode satisfies the tamper-evident, write-once log storage requirement common across all of the above. Switching to COMPLIANCE mode at deploy time provides the strongest posture for environments under continuous audit.

## Deploying

### Prerequisites

The instructions below assume the `aws` CLI, `jq`, and `wg` (WireGuard tools) are available. On Ubuntu/Debian: `sudo apt install wireguard-tools`. On Windows, WSL2 works well.

### Generate a WireGuard Key Pair for Each Site

Each site gateway needs its own key pair. The private key stays on the gateway; only the public key is needed for deployment.

```bash
# Generate a key pair for the first site
wg genkey | tee site1.key | wg pubkey > site1.pub
cat site1.pub
```

> **Security:** Keep private key files secure and never commit them to version control.

### Deploy the Stack

```bash
export PEER1_PUBLIC_KEY=$(cat site1.pub)

aws cloudformation deploy \
  --template-file syslog-cw-s3.template.json \
  --stack-name syslog \
  --capabilities CAPABILITY_IAM \
  --region us-west-2 \
  --parameter-overrides PeerPublicKey=${PEER1_PUBLIC_KEY}

aws cloudformation describe-stacks \
  --stack-name syslog \
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > outputs.json

export SYSLOG_DOMAIN=$(jq -r '.[]|select(.OutputKey=="Domain")|.OutputValue' outputs.json)
export SYSLOG_PORT=$(jq -r '.[]|select(.OutputKey=="Port")|.OutputValue' outputs.json)
export LISTENER_PUBLIC_KEY=$(jq -r '.[]|select(.OutputKey=="ListenerPublicKey")|.OutputValue' outputs.json)

echo "Endpoint:            ${SYSLOG_DOMAIN}:${SYSLOG_PORT}"
echo "Listener public key: ${LISTENER_PUBLIC_KEY}"
```

### Adding More Sites

To add additional site gateways, generate a key pair for each and add a peer entry to the `Peers` list in the `SyslogListener` resource in the template, then redeploy:

```json
{
  "PublicKey": "<site2 public key>",
  "AllowedIPs": ["0.0.0.0/0", "::/0"]
}
```

At scale, peer management should be driven through a CI/CD pipeline — store public keys in a parameter store or secrets manager, generate the `Peers` list programmatically, and trigger a stack update on enrollment or revocation. This keeps key management auditable and avoids manual template edits for each new site.

### Single Stack vs. Multiple Stacks

A single stack can collect from any number of sites simultaneously, but deploying multiple stacks is often the right choice. The primary drivers are:

| Driver | Recommendation |
|---|---|
| **Regional data sovereignty** | Deploy one stack per AWS region. CloudFormation stacks and S3 buckets are region-scoped, so EU logs stay in `eu-west-1`, US logs in `us-east-1`, etc. This is a hard requirement under GDPR and many national cybersecurity frameworks — not optional. |
| **Compliance tier isolation** | PCI-scoped environments need their own KMS key, their own bucket, and potentially `COMPLIANCE`-mode Object Lock. Mixing them with non-PCI sites in one bucket complicates audits and scope definitions. |
| **Differentiated retention** | Dev environments may only need 30 days; production financial systems may need 7 years. These cannot coexist cleanly in one stack without complex S3 lifecycle conditions. |
| **IAM blast radius** | A security team scoped to one business unit should not have access to another unit's logs. Separate stacks with separate buckets are cleaner than prefix-based IAM conditions on a shared bucket. |
| **Cost allocation** | Separate stacks give clean per-business-unit cost attribution without tagging workarounds. |

A practical pattern for most enterprises is **one stack per compliance tier per region** — for example `syslog-pci-eu`, `syslog-pci-us`, `syslog-corp-us`, `syslog-dev-us`. Sites enroll into the stack whose compliance tier and region match their requirements. This keeps the number of stacks manageable while preserving meaningful isolation boundaries.

## Configuring a Site Gateway

After deployment, configure WireGuard on each site gateway using the listener endpoint and public key from the stack outputs.

### Linux Gateway (wg-quick)

Create `/etc/wireguard/syslog.conf`:

```ini
[Interface]
PrivateKey = <contents of site1.key>
Address = 10.200.0.1/32

[Peer]
PublicKey = <ListenerPublicKey from stack outputs>
AllowedIPs = <ListenerTunnelIP>/32
Endpoint = <Domain from stack outputs>:<Port from stack outputs>
PersistentKeepalive = 25
```

> **Split tunnel:** `AllowedIPs` should be set to only the listener's tunnel IP (e.g. `10.200.0.2/32`), not `0.0.0.0/0`. Using `0.0.0.0/0` would route all gateway internet traffic through the WireGuard tunnel, breaking normal network operation. Only syslog traffic directed at the listener's tunnel IP needs to traverse the tunnel. The listener's tunnel IP is assigned by Proxylity and visible in the WireGuard handshake output (`sudo wg show`).

Bring up the tunnel:

```bash
sudo wg-quick up syslog
```

Then configure syslog forwarding. For rsyslog, add to `/etc/rsyslog.conf`:

```
*.* @<listener tunnel IP>:514
```

For syslog-ng:

```
destination d_proxylity { udp("<listener tunnel IP>" port(514)); };
log { source(s_src); destination(d_proxylity); };
```

### Policy-Based Routing (Zero Client Reconfiguration)

To transparently intercept all port-514 traffic on the LAN without reconfiguring individual devices, configure DNAT + PBR on the gateway to redirect UDP/514 toward the listener's tunnel address. Consult your router/firewall vendor documentation for specific syntax (Cisco IOS-XE, Juniper JunOS, MikroTik RouterOS, pfSense/OPNsense, and FortiGate all support this capability).

## Observability

### Log Groups

The stack provisions three CloudWatch log groups:

| Log Group | Purpose |
|---|---|
| `/proxylity-examples/syslog-cw-<stack>` | Live syslog message stream — the primary source for querying and metric filters |
| `/proxylity-examples/syslog-destination-errors-<stack>` | Delivery errors reported by Proxylity for each destination (Firehose and CloudWatch). If a batch fails to deliver, the error is written here. |
| `/aws/kinesisfirehose/<stack>-firehose-logs` | Firehose internal delivery errors — records it could not write to S3 |

Each destination in the Proxylity listener is configured with `LogGroupName` pointing to the destination errors log group, so failed deliveries from both the Firehose and CloudWatch destinations are captured in one place.

### Verifying Delivery

```bash
export SYSLOG_BUCKET=$(jq -r '.[]|select(.OutputKey=="BucketName")|.OutputValue' outputs.json)
export SYSLOG_LOGGROUP=$(jq -r '.[]|select(.OutputKey=="LogGroup")|.OutputValue' outputs.json)

# Check S3 archive (Firehose buffers up to 60s / 50MB before flushing)
aws s3 ls s3://${SYSLOG_BUCKET}/syslog/ --recursive --region us-west-2

# Query live CloudWatch stream
aws logs filter-log-events \
  --log-group-name ${SYSLOG_LOGGROUP} \
  --query "events[].message" \
  --region us-west-2

# Check for destination delivery errors
export SYSLOG_ERROR_LOGGROUP=$(jq -r '.[]|select(.OutputKey=="DestinationErrorLogGroup")|.OutputValue' outputs.json)
aws logs filter-log-events \
  --log-group-name ${SYSLOG_ERROR_LOGGROUP} \
  --region us-west-2
```

### Alarms

The template includes two CloudWatch alarms backed by Logs Metric Filters, both fanning out to a provisioned SNS topic:

| Alarm | Trigger | `TreatMissingData` |
|---|---|---|
| `HighSeverityAlarm` | ≥1 message containing `emerg`, `alert`, or `crit` in any 5-minute window | `notBreaching` |
| `PipelineSilenceAlarm` | Zero messages received in a 1-hour window | `breaching` |

The high-severity alarm fires on the syslog keywords that correspond to RFC 5424 severities 0 (Emergency), 1 (Alert), and 2 (Critical). Severity 3 (`err`) is deliberately excluded to avoid noise — add `?err` to the `HighSeverityMetricFilter` `FilterPattern` if you want errors included.

The silence alarm fires when the pipeline goes quiet — because a site has lost WireGuard tunnel connectivity, the Firehose delivery role has a permissions issue, or the listener is unreachable. Because `TreatMissingData` is `breaching`, a complete gap in metric data (nothing published at all) is treated as a zero count and also triggers the alarm.

### Deploying with Alarm Notifications

Pass an email address during deployment to auto-subscribe to the SNS topic. AWS will send a confirmation email that must be accepted before notifications are delivered.

```bash
aws cloudformation deploy \
  --template-file syslog-cw-s3.template.json \
  --stack-name syslog \
  --capabilities CAPABILITY_IAM \
  --region us-west-2 \
  --parameter-overrides \
      PeerPublicKey=${PEER1_PUBLIC_KEY} \
      AlarmEmail=ops-team@example.com
```

The `AlarmTopicArn` stack output exposes the SNS topic ARN so you can add additional subscriptions (PagerDuty webhook, Lambda function, SMS, etc.) without modifying the template.

```bash
export ALARM_TOPIC=$(jq -r '.[]|select(.OutputKey=="AlarmTopicArn")|.OutputValue' outputs.json)

# Example: add a second email subscriber
aws sns subscribe \
  --topic-arn ${ALARM_TOPIC} \
  --protocol email \
  --notification-endpoint security-team@example.com \
  --region us-west-2
```

## Cross-Account Access for Security Teams

The S3 bucket and CloudWatch log group reside in the deploying account. A central security or SIEM account can be granted read-only access via cross-account IAM without any structural changes to this template.

Add the following statement to the bucket policy (alongside the existing SSL enforcement statement) to allow a designated security account role to read the archive:

```json
{
  "Sid": "AllowSecurityAccountReadAccess",
  "Effect": "Allow",
  "Principal": {
    "AWS": "arn:aws:iam::<SECURITY_ACCOUNT_ID>:role/<SecurityReadRole>"
  },
  "Action": [
    "s3:GetObject",
    "s3:ListBucket"
  ],
  "Resource": [
    "arn:aws:s3:::<BUCKET_NAME>",
    "arn:aws:s3:::<BUCKET_NAME>/*"
  ]
}
```

For CloudWatch Logs, create a cross-account [subscription filter](https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/CrossAccountSubscriptions.html) or grant the security account role `logs:FilterLogEvents` and `logs:DescribeLogStreams` on the log group ARN. SIEM platforms such as Splunk, Elastic, and OpenSearch all support subscription-based ingestion from CloudWatch.

## Retention and Storage Tiering

| Stage | Default | Parameter |
|---|---|---|
| CloudWatch Logs retention | 7 days | `LogGroupRetentionInDays` |
| S3 Standard (hot) | 0–28 days | `LogArchiveTransitionToGlacierInDays` |
| S3 Glacier (cold) | 28–365 days | — |
| S3 expiration / Object Lock retention | 365 days | `LogArchiveExpirationInDays` |
| KMS key rotation | 365 days | `LogArchiveEncryptionKeyRotationInDays` |

## Cost

This solution has no fixed infrastructure cost — there are no servers, agents, or always-on compute resources. All charges are consumption-based:

| Component | Cost driver |
|---|---|
| Proxylity UDP Gateway | Per-message pricing per the Proxylity subscription tier |
| Kinesis Firehose | $0.029/GB ingested (no charge when idle) |
| CloudWatch Logs ingestion | $0.50/GB ingested; retention beyond free tier charged per GB/month |
| S3 Standard storage | ~$0.023/GB/month until Glacier transition |
| S3 Glacier | ~$0.004/GB/month |
| KMS | $1/month per CMK + $0.03 per 10,000 API calls |
| SNS / CloudWatch Alarms | Negligible at syslog volumes |

For a typical enterprise generating 10 GB/day of syslog, expect roughly **$15–30/month** in AWS charges at default retention settings (7-day CloudWatch, 28-day Standard, 365-day Glacier). At 1 GB/day the cost is near-zero. Costs scale linearly with ingestion volume — there are no capacity tiers or reserved minimums to commit to.

## Tearing Down

> **Object Lock:** S3 objects are protected by GOVERNANCE-mode Object Lock. They cannot be deleted without `s3:BypassGovernanceRetention` on your IAM principal. The bucket cannot be deleted while it contains locked objects.

```bash
read -p "Delete ALL contents of '${SYSLOG_BUCKET}'? (y/N) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
  aws s3 rm s3://${SYSLOG_BUCKET} --recursive \
    --region us-west-2 \
    --bypass-governance-retention
fi
```

Once the bucket is empty, delete the stack:

```bash
aws cloudformation delete-stack --stack-name syslog --region us-west-2
```
