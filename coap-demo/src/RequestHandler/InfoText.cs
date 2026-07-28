namespace RequestHandler;

/// <summary>Static informational content for <c>/</c> and <c>/info/*</c>, mirroring the Proxylity website (design.md "/info/*").</summary>
internal static class InfoText
{
    public const string Home = @"Proxylity UDP Gateway
Your UDP workload deserves a serverless backend.

* This site supports CoAP resource discovery (RFC 6690):
coap://proxylity.com/.well-known/core

UDP has always been the right transport. The problem was everything
around it: servers to manage, environments to maintain, infrastructure
to scale. Proxylity removes that entire layer, routing UDP traffic
directly into Lambda, Step Functions, Kinesis, EventBridge, and 12
other AWS services. Zero servers. Zero maintenance.

Deploy in minutes. Scale from zero to millions of packets/second.
Pay only for packets delivered -- not for idle capacity or blocked traffic.

AWS integrations: Lambda, Step Functions, DynamoDB, S3, SQS, SNS,
Kinesis, EventBridge, IoT Core, API Gateway, and more.

Free tier included. No credit card required.
Visit: https://proxylity.com";

    public const string About = @"Proxylity UDP Gateway -- About

Proxylity has maintained >99.99% availability since launch. The
architecture is a deliberate hybrid: Proxylity operates the shared
global infrastructure that terminates UDP and WireGuard connections,
while your data stays entirely in your AWS account. Proxylity does not
store data passing through your listeners -- packets are handed off
and gone.

UDP Gateway carries AWS Qualified Software status, reviewed by AWS
against production-readiness standards, and is listed on AWS
Marketplace with Vendor Insights support for enterprise procurement.

Customers across North America, South America, Europe, and Asia run
RADIUS, CoAP, DNS, syslog, IoT telemetry, and custom UDP workloads.

Founded 2024 * Portland, Oregon * team@proxylity.com
Visit: https://proxylity.com/about";

    public const string Pricing = @"Proxylity UDP Gateway -- Pricing
Available via AWS Marketplace (charges appear on your AWS bill).

Listeners: $0.00139/port-hour (~$1/month each)

Packets (per million, per month):
  0-1M:      Free
  1M-100M:   $1.25/M
  100M-1B:   $1.12/M
  1B-10B:    $0.99/M
  10B-100B:  $0.86/M
  100B-1T:   $0.73/M
  Above 1T:  $0.60/M

Both inbound deliveries and outbound responses are counted.
Firewall-blocked traffic costs nothing. Packets >1KB count as
multiples (rounded up per KB).

Example: 1 Listener + 10M packets/month
  = $1.00 + $11.25 = $12.25/mo (plus AWS service costs)

Pre-paid annual contracts available for volume discounts.
Visit: https://proxylity.com/pricing";

    public const string Features = @"Proxylity UDP Gateway -- Features

Listeners & Transport
- Plain UDP and WireGuard-encrypted listeners
- Packet Sources: server-initiated delivery to clients
- IP/CIDR allowlisting, geographic restrictions

AWS Integrations (14+ services)
- Compute: Lambda, Step Functions
- Data: DynamoDB, S3, Kinesis Streams, Kinesis Firehose
- Messaging: SQS, SNS, EventBridge, IoT Core, API Gateway
- Monitoring: CloudWatch Logs

Performance & Cost
- Intelligent batching: reduce AWS API calls by up to 90%
- Pay only for delivered packets; blocked traffic is free
- Scales from zero to millions of packets/second

Security & Compliance
- Least-privilege IAM per destination
- No Proxylity credentials stored; full CloudTrail audit trail
- External ID protection against confused deputy attacks

Deployment
- CloudFormation and Terraform native; multi-region ready
- >99.99% availability since launch

Visit: https://proxylity.com/features";

    public const string Docs = @"Proxylity UDP Gateway -- Documentation
https://proxylity.com/docs

Getting Started
  Quick Start Guide, Account Connection, Core Concepts

Core Concepts
  Listeners, Destinations, Client Restrictions,
  WireGuard, Packet Sources

AWS Integrations
  Lambda, Step Functions, EventBridge, SQS, SNS,
  DynamoDB, S3, Kinesis, Firehose, CloudWatch Logs,
  API Gateway, IoT Core

Advanced Configuration
  BREX Syntax (binary data extraction),
  Packet JSON format, Batching, Multi-Region,
  Composite Destinations, External API integration

Infrastructure as Code
  CloudFormation: Listener, Destination, Batching,
    PacketSource, IAM Role templates
  Terraform: registry.terraform.io/modules/
    proxylity/udp-gateway/aws/latest

Contact: support@proxylity.com";

    public const string Examples = @"Proxylity UDP Gateway -- Example Projects
https://github.com/proxylity/examples

coap-demo         This demo: multi-region CoAP server with
                  Observe, async responses, global DynamoDB
coap-time-service CoAP time service, Step Functions + Observe
packet-capture    Live packet inspection with streaming UI
radius            Multi-region RADIUS authentication server
momento-udp       UDP-to-cache proxy using Momento
dns-filter        DNS filtering and analysis pipeline
syslog            Syslog aggregation to CloudWatch and S3
wireguard-echo    WireGuard-encrypted echo server
packet-counter    Minimal counter (also Go, Python, C++)
supabase-udp      UDP to Supabase (Postgres) proxy
event-bridge      EventBridge fan-out from UDP
kinesis-streams   Kinesis Data Streams ingestion from UDP";
}
