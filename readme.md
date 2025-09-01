# Proxylity Examples

The folders here contain examples of using our Gateway (UDP as a Service) in various use cases: 

* A simple UDP Packet Counter implemented in [Lambda](packet-counter), and in [Step Functions](packet-counter-sfn).
* A [multi-homed](packet-counter-multi-region) (region) version of Packet Counter demonstrating the use of Destinations with region-specific ARNs.
* A service for receiving [log messages](syslog) over UDP and directing them to both CloudWatch and S3 via Firehose, all with no code.
* Combining HTTP browser interactions with UDP interactions in a [multi-modal flow](multi-modal) featuring long-running tasks using API Gateway in conjunction with UDP Gateway.
* A DNS resolver implementing [DNS Filtering](dns-filter) (domain blocking and redirection) for bespoke DNS in your own AWS account.
* A [WireGuard Backend](./wireguard-echo/readme.md) that supports UDP echo and ICMP ping through an encrypted tunnel (without a server, of course).
* A remote temperature sensor [IoT Client Device](./wireguard-iot-device/README.md) based on the "Cheap Yellow Display"<sup>[1](https://github.com/witnessmenow/ESP32-Cheap-Yellow-Display)[2](https://randomnerdtutorials.com/cheap-yellow-display-esp32-2432s028r/)</sup> and using [WireGuard-ESP32](https://github.com/ciniml/WireGuard-ESP32-Arduino) that sends readings to a UDP Gateway backend.

Enjoy!
