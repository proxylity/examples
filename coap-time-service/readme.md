# CoAP Time Service

This example demonstrates a basic [CoAP](https://en.wikipedia.org/wiki/Constrained_Application_Protocol) API implemented with Proxylity UDP Gateway and AWS Step Functions. The service responds to CoAP `GET /time` requests with the current UTC time. Confirmable (`CON`) requests to any other path receive a `4.04 Not Found` ACK so clients are not left retransmitting; Non-confirmable (`NON`) requests to unknown paths are silently ignored. 

It is a minimal illustration of how to route CoAP requests, in this case using a serverless state machine (Lambda would be another great option).

This example demonstrates:

* Using the `coap` option for a Destination `formatter` to automatically decode binary CoAP packets into JSON before delivery.
* Routing CoAP requests by path inside an AWS Step Functions Express state machine (no cold start!). A simple to extend approach.
* Handling CoAP Confirmable vs Non-confirmable semantics

## System Diagram

<img src="architecture.svg" title="CoAP Time Service Architecture" width=600 />

## How It Works

Proxylity's `coap` formatter decodes each inbound binary UDP/CoAP packet into a JSON object and delivers it as the `Data` field of each message:

```jsonc
// Example decoded CoAP GET /time request delivered to the state machine
{
  "Messages": [
    {
      "Tag": "abc123",
      "Remote": { "IpAddress": "203.0.113.5", "Port": 12345 },
      "Data": "{\"Version\":1,\"Type\":0,\"Code\":1,\"MessageId\":4711,\"Token\":\"AQID\",\"Method\":\"GET\",\"Path\":\"/time\",\"Options\":[{\"Number\":11,\"Value\":\"dGltZQ==\"}]}"
    }
  ]
}
```

The state machine inspects the `Path` property of each decoded request:

- Path is `/time` → reply with `2.05 Content` ACK, payload is the execution start time as plain text.
- Any other path, `Type` is `0` (CON) → reply with `4.04 Not Found` ACK. This is required by RFC 7252: a Confirmable message **must** be acknowledged or the sender will keep retransmitting.
- Any other path, `Type` is `1` (NON) → no reply. Non-confirmable messages are fire-and-forget; silently ignoring is correct per spec.

The reply `Data` is a stringified JSON object that Proxylity's `coap` formatter re-encodes into a valid binary CoAP response before sending it back to the client:

```jsonc
// CoAP response produced for GET /time
{
  "Type": 2,
  "Code": "2.05",
  "MessageId": 4711,
  "Token": "AQID",
  "Options": [
    {"Number": 12, "Value": ""},
    {"Number": 14, "Value": ""}
  ],
  "Payload": "<base64-encoded UTC timestamp>"
}
```

## Deploying

> **NOTE**: The instructions below assume the AWS SAM CLI (`sam`) and `aws` CLI are installed, and that you are subscribed to Proxylity UDP Gateway.

Deploy using SAM (required because the template references the external `state-machine.asl.json` file):

```bash
sam build && sam deploy --guided
```

Once deployed, retrieve the endpoint from the stack outputs:

```bash
aws cloudformation describe-stacks \
  --stack-name coap-time-service \ # or your chosen stack name
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > outputs.json

COAP_DOMAIN=$(jq -r '.[]|select(.OutputKey=="Domain")|.OutputValue' outputs.json)
COAP_PORT=$(jq  -r '.[]|select(.OutputKey=="Port") |.OutputValue' outputs.json)
```

For a very basic test we can send a GET request with `echo`, `nc` and `strings`:

```bash
echo -ne "\x50\x01\x12\x34\xB4\x74\x69\x6D\x65" | nc -u ${COAP_DOMAIN} ${COAP_PORT} -w 1 | strings
```

### Testing with `coap-client`

If you have [libcoap](https://libcoap.net/) with the [`coap-client`](https://libcoap.net/doc/reference/4.2.0/man_coap-client.html) tool installed:

```bash
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/time"
```

The response payload is a plain-text ISO 8601 UTC timestamp, for example:

```
2026-04-14T12:34:56.789Z
```

A CON request to any other path (e.g. `coap-client -m get "coap://…/foo"`) will receive a `4.04 Not Found` ACK. A NON request to an unknown path receives no reply.

### Removing the stack

```bash
sam delete
```

## State Machine Implementation

![CoAP Service State Machine](./state-machine.svg)

The state machine (`state-machine.asl.json`) uses a **Map** state to process each incoming message independently, followed by a **Pass** state that collects the results:

```
Process Messages (Map)
  └─ Parse CoAP Data (Pass)    →  stringified Data into .Coap field
       └─ Route by Path (Choice)
          ├─ Path = "/time"    → Respond With Time (Pass) 
          |                      2.05 Content ACK
          └─ (default)         → Check Message Type (Choice)
              ├─ Type = "CON"  → Respond With Not Found (Pass) 
              |                  4.04 Not Found ACK
              └─ (default)     → No Reply (Pass) 
                                 null
Collect Replies (Pass)         → filter out nulls,
                                 wrap in {"Replies": [...]}
```

### Parse CoAP Data

The `Data` field arrives as a stringified JSON string. This state uses JSONata's `$parse()` to deserialise it into a structured `.Coap` property so all downstream states can reference fields like `.Coap.Path` and `.Coap.Type` directly.

### Route by Path

A `Choice` state with an explicit condition for the `\time` path (additional paths would be added here). Any path not matched falls through to `Default`:

| Condition | Next state |
|---|---|
| `Coap.Path = "/time"` | Respond With Time |
| _(anything else)_ | Check Message Type |

### Check Message Type

A second `Choice` state that enforces CoAP's Confirmable/Non-confirmable contract (RFC 7252 §4.2):

| Condition | Next state | Rationale |
|---|---|---|
| `Coap.Type = 0` | Respond With Not Found | CON (Type=0) requires an ACK or the client retransmits |
| _(NON or other)_ | No Reply | Fire-and-forget; silence is correct |

### Collect Replies

The Map state produces one output per message — either a reply object or `null` (from **No Reply**). This final `Pass` state filters the nulls and wraps the rest:

```jsonata
{"Replies": $states.input[$ != null]}
```
