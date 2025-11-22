# Proxylity Examples

## Transform UDP with Serverless-First Architecture

Traditional UDP services require dedicated servers, complex load balancing, and constant infrastructure management. [**Proxylity UDP Gateway**](https://proxylity.com/features.html) revolutionizes this approach by bringing UDP into the modern serverless ecosystem, allowing you to build highly scalable, cost-effective UDP applications that automatically scale from zero to millions of packets per second.

### Why UDP Gateway Changes Everything

As a software architect or developer, you understand the challenges of building UDP-based systems:
- **Infrastructure Complexity**: Managing dedicated UDP servers, load balancers, and auto-scaling groups
- **Cost Inefficiency**: Paying for idle capacity during low-traffic periods
- **Operational Overhead**: Monitoring, patching, and maintaining always-on infrastructure
- **Integration Friction**: Bridging UDP protocols with modern cloud-native services

UDP Gateway eliminates these pain points by providing **UDP as a Service** - serverless UDP processing that integrates seamlessly with AWS Lambda, Step Functions, EventBridge, and other managed services. Your UDP traffic is automatically captured, routed, and processed using the same event-driven patterns you already use for HTTP APIs.

### Real-World Integration Patterns

The examples below demonstrate proven architectural patterns that solve actual business challenges. Each example is production-ready and showcases different aspects of serverless UDP architecture - from simple packet processing to complex multi-modal workflows and secure tunneling solutions.

Whether you're building IoT telemetry systems, real-time gaming backends, DNS services, or VPN solutions, these examples provide the blueprints for implementing UDP services that are:
- ✅ **Serverless-first** - No infrastructure to manage
- ✅ **Auto-scaling** - Handle traffic spikes effortlessly  
- ✅ **Cost-optimized** - Pay only for packets processed
- ✅ **Cloud-native** - Integrate with your existing AWS services

## Example Solutions

* **[UDP Packet Counter](packet-counter)** - A foundational example implemented in Lambda, with a [Step Functions variant](packet-counter-sfn). Also available in [Go](packet-counter-go), [C++](packet-counter-cpp), and [Python](packet-counter-python)
* **[Multi-Region Packet Counter](packet-counter-multi-region)** - Demonstrates global UDP processing with region-specific routing and failover capabilities
* **[Syslog to Cloud](syslog)** - Enterprise-grade log ingestion over UDP, routing to CloudWatch Logs and S3 via Firehose - completely code-free
* **[EventBridge Integration](event-bridge)** - Event-driven UDP processing showcasing how to integrate UDP traffic with AWS's event backbone
* **[SQS Queues](sqs)** - Demonstrates directing UDP packets to SQS queues (standard and FIFO) with configurable delivery options and message attributes
* **[Multi-Modal Workflows](multi-modal)** - Sophisticated example combining HTTP browser interactions with UDP processing and long-running tasks
* **[DNS Filtering Service](dns-filter)** - Production-ready DNS resolver with domain blocking and redirection capabilities for custom DNS infrastructure
* **[WireGuard VPN Backend](./wireguard-echo/readme.md)** - Serverless VPN solution supporting UDP echo and ICMP ping through encrypted tunnels
* **[IoT Temperature Sensor](./wireguard-iot-device/README.md)** - End-to-end IoT solution featuring a "Cheap Yellow Display"<sup>[1](https://github.com/witnessmenow/ESP32-Cheap-Yellow-Display), [2](https://randomnerdtutorials.com/cheap-yellow-display-esp32-2432s028r/)</sup> device with [WireGuard-ESP32](https://github.com/ciniml/WireGuard-ESP32-Arduino) sending secure telemetry
* **[UDP to REST API](./udp-to-http/readme.md)** - Demonstrates using an "inside out" API Gateway to proxy UDP sensor data to [Adafruit IO](https://io.adafruit.io)'s REST API.

---

## Ready to Build Serverless UDP Solutions?

Transform your UDP architecture today with Proxylity UDP Gateway. Get started with a free trial, or on the free tier and see how serverless UDP can simplify your infrastructure while reducing costs.

[![Get Proxylity UDP Gateway on AWS Marketplace](https://img.shields.io/badge/AWS%20Marketplace-Get%20Started-orange?style=for-the-badge&logo=amazonwebservices)](https://aws.amazon.com/marketplace/pp/prodview-cpvl5wgt2yo2e?sr=0-1&ref_=beagle&applicationId=AWSMPContessa)

---

<small>*Proxylity and UDP Gateway are trademarks of Proxylity LLC. AWS, Lambda, Step Functions, EventBridge, and CloudWatch are trademarks of Amazon.com, Inc. WireGuard is a trademark of Jason A. Donenfeld.*</small>
