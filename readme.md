# Proxylity Examples

The folders here contain examples of using UDP Gateway to implement UDP services. 

* Packet Counter implemented in [Lambda](packet-counter), and in [Step Functions](packet-counter-sfn).
* A [multi-homed](packet-counter-multi-region) (region) version of Packet Counter demonstrating the use of Destinations with region-specific ARNs.
* Receiving [log messages](syslog) over UDP and directing them to both CloudWatch and S3 via Firehose, all with no code.