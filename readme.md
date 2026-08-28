# Proxylity Examples

## Transform UDP with Serverless-First Architecture

Traditional UDP services require dedicated servers, complex load balancing, and constant infrastructure management. [**Proxylity UDP Gateway**](https://proxylity.com/features.html) revolutionizes this approach by bringing UDP into the modern serverless ecosystem, allowing you to build highly scalable, cost-effective UDP applications that automatically scale from zero to millions of packets per second.

### Why UDP Gateway Changes Everything

As a software architect or developer, you understand the challenges of building UDP-based systems:

- **Infrastructure Complexity**: Managing servers and clusters, load balancers, auto-scaling groups, health-checks and failover
- **Cost Inefficiency**: Paying for idle capacity during low-traffic periods
- **Operational Overhead**: Monitoring, patching, and maintaining always-on infrastructure
- **Integration Friction**: Bridging UDP protocols with modern cloud-native services

UDP Gateway eliminates these pain points by providing **UDP as a Service** - serverless UDP processing that integrates seamlessly with AWS Lambda, Step Functions, EventBridge, and other managed services. Your UDP traffic is automatically routed, and processed using the same event-driven patterns you already depend on in your modern architecture.

### Real-World Integration Patterns

The examples below demonstrate patterns that can be applied to solve business challenges. Each example showcases different aspects of serverless networking architecture - from simple packet processing to multi-modal workflows to secure tunnelled solutions.

Whether you're building telemetry systems, real-time backends, enterprise services, or VPN solutions, these examples provide the blueprints for implementing services that are:

- ✅ **Low Maintenance** - No infrastructure to manage
- ✅ **Auto-scaling** - Handle wide-ranging traffic volumes effortlessly
- ✅ **Resilient** - Layered redundancy and global scale
- ✅ **Cost-optimized** - Pay only for packets processed
- ✅ **AWS-native** - Integrate with your existing AWS practices

## Example Solutions

* **[UDP Packet Counter](packet-counter)** - An introductory example implemented in Lambda, with a [Step Functions variant](packet-counter-sfn). Also available in [Go](packet-counter-go), [C++](packet-counter-cpp), and [Python](packet-counter-python)
* **[Syslog to Cloud](syslog)** - Enterprise-grade log ingestion over UDP, routing to CloudWatch Logs and S3 via Firehose - completely code-free
* **[Packet Capture](packet-capture)** - Live, browser-based UDP packet capture with real-time display via AppSync Events. Supports plain UDP and WireGuard ingress with no persistent storage. A [deployment walkthrough](https://youtu.be/BUfWxlaHTWo) is available on YouTube.
* **[SQS Queues](sqs)** - Demonstrates directing UDP packets to SQS queues (standard and FIFO) with configurable delivery options and message attributes
* **[Kinesis Data Streams](kinesis-streams)** - Directs UDP packets to Amazon Kinesis Data Streams with a dynamic partition key derived from the packet payload
* **[EventBridge Integration](event-bridge)** - Event-driven UDP processing showcasing how to integrate UDP traffic with AWS's event backbone
* **[Location Service](location-service)** - No-code device location tracking with UDP Gateway and AWS Location Service
* **[MAVLink to FIFO SQS](mavlink-fifo)** - Delivers MAVLink v1 and v2 drone telemetry packets to a FIFO SQS queue, demonstrating message deduplication and ordered delivery
* **[Multi-Region Packet Counter](packet-counter-multi-region)** - Demonstrates global UDP processing with region-specific routing and failover capabilities
* **[Multi-Modal Workflows](multi-modal)** - Sophisticated example combining HTTP browser interactions with UDP processing and long-running tasks
* **[Momento Cache over UDP](momento-udp)** - Superior performance for GET/SET operations for Momento Cache when network connections degrade
* **[Supabase over UDP](supabase-udp)** - Calling Supabase via UDP from the edge to integrate IoT devices with your web app
* **[UDP to REST API](udp-to-http)** - Demonstrates using an "inside out" API Gateway to proxy UDP sensor data to [Adafruit IO](https://io.adafruit.io)'s REST API
* **[CoAP Time Service](coap-time-service)** - UDP Gateway speaks [CoAP](https://en.wikipedia.org/wiki/Constrained_Application_Protocol) — the REST-like protocol purpose-built for constrained IoT devices. Binary packets are decoded to JSON by the `coap` formatter before delivery, and re-encoded on the way back. A serverless Step Functions state machine routes requests by URI path and handles Confirmable/Non-confirmable semantics correctly, with zero infrastructure to manage.
* **[CoAP Demo](coap-demo)** - A production-quality multi-region CoAP API demonstrating the full breadth of UDP Gateway's CoAP support: Observe notifications, Block1/Block2 transfers, async separate responses, and a live exercisable endpoint spanning multiple AWS regions
* **[DNS Filtering Service](dns-filter)** - Production-ready DNS resolver with domain blocking and redirection capabilities for custom DNS infrastructure
* **[WireGuard Echo Service](wireguard-echo)** - Serverless solution supporting UDP echo and ICMP ping through encrypted tunnels
* **[IoT Temperature Display/Sensor](wireguard-iot-device)** - IoT device based on a "Cheap Yellow Display" ([1](https://github.com/witnessmenow/ESP32-Cheap-Yellow-Display), [2](https://randomnerdtutorials.com/cheap-yellow-display-esp32-2432s028r/)) with [WireGuard-ESP32](https://github.com/ciniml/WireGuard-ESP32-Arduino) protected telemetry and time synchronization (NTP)
* **[RADIUS Authorization and Accounting](radius)** - Cloud-based RADIUS authentication and accounting system with session state tracking, packet archiving, and multi-region deployment support. Like FreeRADIUS and NPS if they were modern, scalable and had super powers.

---

## Ready to Build a Serverless UDP Solution?

Transform your UDP architecture today with Proxylity UDP Gateway. Get started with a free trial, or on the free tier and see how serverless UDP can simplify your infrastructure while reducing costs.

[![Get Proxylity UDP Gateway on AWS Marketplace](https://img.shields.io/badge/AWS%20Marketplace-Get%20Started-orange?style=for-the-badge&logo=amazonwebservices)](https://aws.amazon.com/marketplace/pp/prodview-cpvl5wgt2yo2e?sr=0-1&ref_=beagle&applicationId=AWSMPContessa)

---

<small>*Proxylity and UDP Gateway are trademarks of Proxylity LLC. AWS, Lambda, Step Functions, EventBridge, and CloudWatch are trademarks of Amazon.com, Inc. WireGuard is a trademark of Jason A. Donenfeld.*</small>
