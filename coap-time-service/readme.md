# CoAP Time Service

This example demonstrates a [CoAP](https://en.wikipedia.org/wiki/Constrained_Application_Protocol) API with Observe ([RFC 7641](https://datatracker.ietf.org/doc/html/rfc7641)) and Discovery ([RFC 6690](https://datatracker.ietf.org/doc/html/rfc6690)) implemented with Proxylity UDP Gateway and AWS Step Functions. 

The service responds to CoAP `GET /time` requests with the current UTC time. Confirmable (`CON`) requests to any other path receive a `4.04 Not Found` ACK so clients are not left retransmitting; Non-confirmable (`NON`) requests to unknown paths are silently ignored. It demonstrates how to setup CoAP formatting of requests and implement Discovery and Observe using a serverless architecture with an AWS Step Functions state machine providing the handler logic (of course, Lambda would be another great option).

For security, CoAP+wg (CoAP in Wireguard) is implemented as a more efficient alternative to DTLS to protect messages in transit. 

This example project demonstrates:

* Using the `coap` option for a Destination `formatter` to automatically decode binary CoAP packets into JSON before delivery.
* Routing CoAP requests by path inside an AWS Step Functions Express state machine (no cold start!). A simple to extend approach.
* Handling CoAP Confirmable vs Non-confirmable semantics
* Implementing CoAP Discovery (RFC 6690 CoRE Link Format) to advertise available resources.
* Implementing Observe (RFC 7641) to provide asynchronous server-sent updates via a DynamoDB-backed subscription table and a Lambda notifier.
* Using CoAP+wg to protect requests on the wire (more efficient and simpler than DTLS or other alternatives)

## System Diagram

<img src="architecture.svg" title="CoAP Time Service Architecture" width=600 />

## How It Works

Proxylity's `coap` formatter decodes each inbound binary UDP packet into a JSON object representation of the CoAP content and delivers it as the `Data` field of each message:

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

The state machine evaluates each decoded message's `Type` first, then dispatches by `Path`:

- `Type = 2` (ACK) → silently discarded; acknowledgements of outbound NON notifications require no action.
- `Type = 3` (RST) → removes the matching Observe subscription from DynamoDB (identified by `ClientEndpoint` + `Token`), then discards.
- `Path = "/.well-known/core"` → RFC 6690 resource discovery; replies with `2.05 Content` and a CoRE Link Format payload advertising the `/time` resource and its `obs` attribute.
- `Path = "/time"`, **Observe=0** option → registers the client as an Observe subscriber in DynamoDB, then confirms with a `2.05 Content` ACK (carrying Option 6 and Option 14 `Max-Age=60`).
- `Path = "/time"`, **Observe=1** option → deregisters the client, then replies with a plain `2.05 Content` ACK.
- `Path = "/time"`, no Observe option → one-shot GET; replies with a `2.05 Content` ACK containing the current UTC time.
- Any other path, CON → `4.04 Not Found` ACK (RFC 7252 §4.2 — all Confirmable messages must be acknowledged).
- Any other path, NON → no reply (fire-and-forget; silence is correct per RFC 7252).

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

> **NOTE**: The instructions below assume the AWS SAM CLI (`sam`) and `aws` CLI are installed, `wireguard-tools` is available, and that you are **subscribed to Proxylity UDP Gateway at the Pro or Enterprise level** (packet sourcing is not available on Free plans).

Generate a WireGuard key pair for your client:

```bash
wg genkey | tee client.key | wg pubkey > client.pub
```

Deploy using SAM (required because the template references the external `state-machine.asl.json` file), passing your client's public key:

```bash
sam build && sam deploy --guided \
  --parameter-overrides WireGuardClientPublicKey=$(cat client.pub)
```

Once deployed, retrieve the endpoint and the listener's WireGuard public key from the stack outputs:

```bash
aws cloudformation describe-stacks \
  --stack-name coap-time-service \
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > outputs.json

export COAP_DOMAIN=$(jq -r '.[]|select(.OutputKey=="Domain")          |.OutputValue' outputs.json)
export COAP_PORT=$(jq  -r '.[]|select(.OutputKey=="Port")             |.OutputValue' outputs.json)
export WG_PEER_KEY=$(jq -r '.[]|select(.OutputKey=="WireGuardPublicKey")|.OutputValue' outputs.json)
```

Create a WireGuard tunnel configuration:

```bash
sudo tee /etc/wireguard/coap.conf <<EOF
[Interface]
PrivateKey = $(cat client.key)
Address = 10.10.10.10/32

[Peer]
PublicKey = ${WG_PEER_KEY}
Endpoint = ${COAP_DOMAIN}:${COAP_PORT}
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
EOF
sudo wg-quick up coap
```

### Testing with `coap-client`

If you have [libcoap](https://libcoap.net/) with the [`coap-client`](https://libcoap.net/doc/reference/4.2.0/man_coap-client.html) tool installed, send a request through the WireGuard tunnel:

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
sudo wg-quick down coap
sam delete
```

## State Machine Implementation

![CoAP Service State Machine](./state-machine.svg)

The state machine (`state-machine.asl.json`) uses a **Map** state to process each incoming message independently, followed by a **Pass** state that collects the results:

```
Process Messages (Map)
  └─ Parse CoAP Data (Pass)           → deserialise Data→.Coap; compute ClientEndpoint
     └─ Route by Type (Choice)
        ├─ Type 3 (RST)
        │   └─ Check RST Token (Choice)
        │       ├─ Token present       → Handle RST (Task)       → No Reply (RST)
        │       └─ (no token)          → No Reply (RST)
        ├─ Type 2 (ACK)                → No Reply (Client ACK)
        └─ (default)
            └─ Route by Path (Choice)
               ├─ "/.well-known/core" → Respond With Discovery (Pass)
               │                           2.05 CoRE link-format
               ├─ "/time"
               │   └─ Route by Observe (Choice)
               │       ├─ Observe=0   → Register Observe (Task)   → Respond With Time Observed
               │       ├─ Observe=1   → Deregister Observe (Task) → Respond With Time
               │       └─ (default)   → Respond With Time
               ├─ Type 0 (CON)         → Respond With Not Found (Pass)  4.04
               └─ (default)           → No Reply (Unknown Path)
Collect Replies (Pass)                → filter nulls, wrap {"Replies": [...]}
```

### Parse CoAP Data

The `Data` field arrives as a stringified JSON string. This state uses JSONata's `$parse()` to deserialise it into a structured `.Coap` property so all downstream states can reference fields like `.Coap.Path` and `.Coap.Type` directly. It also pre-computes `ClientEndpoint` (an `IP:Port` string) once so the DynamoDB Task states do not repeat that expression.

### Route by Type

The first `Choice` state handles CoAP message-type bookkeeping before any path routing. Inbound ACK messages are acknowledgements of our outbound NON notifications and need no action. RST messages signal that a client is cancelling an Observe subscription or rejecting a CON — they are routed into the RST sub-flow. All other types (CON and NON requests) fall through to path routing.

| Condition | Next state |
|---|---|
| `Coap.Type = 3` (RST) | Check RST Token |
| `Coap.Type = 2` (ACK) | No Reply (Client ACK) |
| _(default)_ | Route by Path |

### Check RST Token / Handle RST

RFC 7252 §4.5 states that a RST message SHOULD echo the Token of the triggering message. **Check RST Token** is a `Choice` state: if no Token is present the subscription cannot be identified and the message is silently dropped. If a Token is present, **Handle RST** issues a `dynamodb:DeleteItem` to remove the matching subscription (keyed on `ClientEndpoint` + `Token`). Either way the result is a null output — RST is a one-way signal and requires no outbound reply.

### Route by Path

A `Choice` state that dispatches on the decoded URL path. CON requests to an unknown path must be acknowledged per RFC 7252 §4.2; NON requests to unknown paths are silently dropped.

| Condition | Next state |
|---|---|
| `Coap.Path = "/.well-known/core"` | Respond With Discovery |
| `Coap.Path = "/time"` | Route by Observe |
| `Coap.Type = 0` (CON) | Respond With Not Found |
| _(default)_ | No Reply (Unknown Path) |

### Respond With Discovery

Returns a `2.05 Content` ACK with Content-Format 40 (`application/link-format`) whose payload is the RFC 6690 CoRE Link Format listing for the `/time` resource:

```
</time>;rt="time";if="clock";ct=0;obs
```

The `obs` attribute advertises that `/time` supports RFC 7641 Observe, allowing CoAP clients and proxies to discover this capability via a standard GET `/.well-known/core` request.

### Route by Observe

A `Choice` state that inspects CoAP Option 6 (Observe) on a `/time` request to decide whether the client is registering, deregistering, or making a plain one-shot GET. Option values are base64-encoded per the Proxylity CoAP formatter; the zero integer encodes to an empty byte string `""` and 1 encodes to `"AQ=="`.

| Condition | Next state |
|---|---|
| Option 6 present, value `""` (Observe=0, register) | Register Observe |
| Option 6 present, value `"AQ=="` (Observe=1, deregister) | Deregister Observe |
| _(default — no Observe option)_ | Respond With Time |

### Register Observe

A `Task` state that writes a subscription record to the `ObserveTable` DynamoDB table via `dynamodb:PutItem`. The record stores:

- **`ClientEndpoint`** (partition key) — the client's `IP:Port` string.
- **`Token`** (sort key) — the CoAP token that will be echoed in all notifications.
- **`Remote`** — a serialised JSON object with `Address`, `Port`, and `PeerKey` fields that the ObserveNotifier Lambda uses directly to address outbound packets.
- **`Expires`** — a Unix epoch TTL set to `now + 180 s`. Clients must re-register within this window; each notification carries `Max-Age: 60` so a well-behaved client re-registers after at most three missed cycles.

On failure the Catch falls back to **Respond With Time** without Option 6 — the RFC 7641 §3.1 way to decline a registration: the client receives the time it requested and understands the subscription was not accepted.

On success the state transitions to **Respond With Time Observed**.

### Deregister Observe

A `Task` state that removes the subscription via `dynamodb:DeleteItem` (keyed on `ClientEndpoint` + `Token`). On failure the Catch falls back to **Respond With Time** — the subscription may linger until the 180 s TTL expires, which is acceptable as a self-healing condition. Either way the client receives a plain `2.05 Content` time response.

### Respond With Time / Respond With Time Observed

Both states emit a `2.05 Content` ACK with the execution start time (ISO 8601 UTC) as the plain-text payload. **Respond With Time Observed** additionally includes Option 6 (`Observe=0`) to confirm the subscription and Option 14 (`Max-Age=60`) to tell the client how frequently it should expect notifications and when to re-register.

### Respond With Not Found

Returns a `4.04 Not Found` ACK. Required by RFC 7252 §4.2: a Confirmable message that goes unacknowledged will be retransmitted by the client indefinitely.

### Collect Replies

The Map state produces one output per message — either a reply object or `null` (from **No Reply**). This final `Pass` state filters the nulls and wraps the rest:

```jsonata
{"Replies": $states.input[$ != null]}
```

## Observe Notifier

The `ObserveNotifierFunction` Lambda runs every minute on an EventBridge Scheduler schedule. It:

1. **Scans** `ObserveTable` for items whose `Expires` timestamp has not yet passed (active subscriptions).
2. **Builds** a CoAP NON (Type=1) `2.05 Content` notification for each subscriber, carrying:
   - **Option 6** (Observe) — a 24-bit sequence number derived from `UnixEpoch mod 2²⁴`. Since exactly 60 s elapses between sends, each value is strictly greater than the previous within RFC 7641 §4.4's 128-second comparison window — no persistent counter needed.
   - **Option 12** (Content-Format=0) — plain text.
   - **Option 14** (Max-Age=60) — signals the client to re-register within 60 seconds.
   - **Payload** — ISO 8601 UTC timestamp.
3. **Publishes** each notification to the `ObserveNotifyTopic` SNS topic, wrapped in a Proxylity outbound envelope. The `ObservePacketSource` custom resource subscribes Proxylity to that topic, causing it to re-encode and deliver the CoAP packets directly to the registered clients.

DynamoDB TTL on `Expires` provides eventual hard cleanup of any subscriptions the Lambda never saw deregistered (for example, if a client disappears without sending RST or Observe=1).

> **NOTE**: Using DynamoDB **Scan** can be (almost always is) a bad idea. In this solution a large number of clients using the Observe functionality could be expensive. Following this pattern should be done with great care (it's okay for small numbers). A better (but more complex) option would be to use Valkey Serverless.

As always, feel free to reach out on our [website](https://proxylity.com/contact) or via [email](mailto:support@proxylity.com?subject=CoAP%20Example%20Repo) with questions and comments.