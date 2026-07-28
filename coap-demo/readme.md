# CoAP Demo

A production-quality reference [CoAP](https://en.wikipedia.org/wiki/Constrained_Application_Protocol) API built on Proxylity UDP Gateway, deployed across multiple AWS regions. It's a live, exercisable endpoint demonstrating both CoAP as a modern RESTful protocol over UDP, and the capabilities of UDP Gateway itself (the `coap` destination formatter, Lambda destinations, Packet Sources, and DynamoDB Streams-driven event delivery).

The Lambda never parses or serializes CoAP packets directly -- UDP Gateway's `coap` destination formatter handles all protocol translation, delivering each request as a structured JSON object and accepting a structured JSON object back for the response.

> **NOTE**: Deploying this example requires an active Proxylity UDP Gateway subscription. Packet Sources (used here for CoAP Observe notifications and async/separate responses) are not available on the Free plan.

## Architecture

```
                     CoAP Client
                          │
                          ▼
              UDP Gateway Listener (global)
                          │
              coap destination formatter
                          │
                          ▼
        RequestHandler Lambda (region closest to client)
             │                              │
             ▼                              ▼
     Regional DynamoDB Table        DynamoDB Global Table
     (per-request writes,           (Observe subscriptions,
      Block1 upload scratch)         aggregated /system state)
             │                              │
             ▼ stream                       ▼ stream (per-region replica)
   RegionalAggregator Lambda         GlobalNotifier Lambda
   (ADD counters / forward           (recompute + broadcast to this
    time into Global Table)           region's own Observe subscribers)
                                              │
                                              ▼
                                   Packet Source (SNS) ──▶ CoAP Client
                                              ▲
                                              │
                                  AsyncResponder Lambda
                                  (fired by a one-off EventBridge
                                   Scheduler schedule, for /coap/async)
```

Two CloudFormation templates implement this, matching [design.md](./design.md)'s "Multi-Region Deployment" section:

- **[templates/global.template.json](./templates/global.template.json)** (deployed once) -- the DynamoDB Global Table (Observe registry and aggregated `/system` state, replicated to every region) and the UDP Gateway Listener.
- **[templates/region.template.json](./templates/region.template.json)** (deployed once per region) -- the four Lambda functions, the regional DynamoDB table, the region's own Destination ARN registration, the outbound Packet Source SNS topic, and the EventBridge Scheduler group used for async responses.

## Resource Layout

```
/
├── /.well-known/core
├── /info/{about,pricing,features,docs,examples}
├── /contact
├── /coap/{con,non,async,binary,large,echo,observers}
├── /demo/{request,region,ping,upload}
└── /system/{health,version,metrics,time}
```

See [design.md](./design.md) for a full description of each resource, content negotiation rules, and the CoAP mechanics (Confirmable/Non-confirmable, Observe, Block1/Block2, separate responses) each one demonstrates.

## How It Works

- **Request/response**: UDP Gateway's `coap` formatter decodes each inbound packet into a `CoapRequest` JSON object and delivers it to `RequestHandler`, which returns a `CoapResponse` JSON object that the formatter re-encodes to wire bytes.
- **Observe** ([RFC 7641](https://datatracker.ietf.org/doc/html/rfc7641)): subscriptions are persisted in the Global Table rather than held as live connections. State changes (an Observe subscription changing, or `/system/*` state changing) are picked up from DynamoDB Streams and pushed to clients as events, never by polling.
- **Async/separate responses** ([RFC 7252 §5.2.2](https://datatracker.ietf.org/doc/html/rfc7252#section-5.2.2)): `/coap/async` ACKs immediately, then `RequestHandler` creates a one-off, self-deleting EventBridge Scheduler schedule that invokes `AsyncResponder`, which delivers the deferred response via the Packet Source.
- **Multi-region**: each region runs its own copy of all four Lambdas and its own regional DynamoDB table. `RequestHandler` writes every request to its regional table; `RegionalAggregator` folds those writes into the Global Table with an atomic, commutative `ADD` (never a full-item `PUT`, which would let concurrent regions overwrite each other); `GlobalNotifier` reacts to the Global Table's own regional replica stream and notifies only the subscribers *that region* owns (tracked via each subscription's `LastWriteRegion` attribute), so every region's subscribers get the same globally-consistent values without duplicate deliveries. `/demo/region` reports which region actually handled a given request.

## Deploying

> **NOTE**: Requires the AWS SAM CLI (`sam`), the `aws` CLI, and `jq`.

Deploying and tearing down follow the two-stage pattern used by the other multi-region examples in this repo (see [radius](../radius) and [dns-filter](../dns-filter)):

```bash
cd coap-demo
./scripts/deploy.sh
```

This deploys `templates/global.template.json` once (to the region in `$AWS_REGION`, default `us-west-2`), captures its outputs into `global-outputs.json`, then builds and deploys `templates/region.template.json` to every region in `$DEPLOY_TO_REGIONS` (default `us-west-2 us-east-1 eu-west-1`). Edit [scripts/configure.sh](./scripts/configure.sh) to change the regions, allowed client CIDR, stack name, or log level -- if you change the region list, also update the `Replicas` list on the `GlobalTable` resource in `templates/global.template.json` to match.

Retrieve the endpoint from the global stack's outputs:

```bash
export COAP_DOMAIN=$(jq -r '.Domain' global-outputs.json)
export COAP_PORT=$(jq -r '.Port' global-outputs.json)
```

## Testing with `coap-client`

If you have [libcoap](https://libcoap.net/) with the [`coap-client`](https://libcoap.net/doc/reference/4.2.0/man_coap-client.html) tool installed:

```bash
# Resource discovery
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/.well-known/core"

# Confirmable / non-confirmable demos
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/coap/con"
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/coap/non"

# Which region handled this request?
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/demo/region"

# Observe /system/time -- prints an updated notification on every broadcast
coap-client -m get -s 30 "coap://${COAP_DOMAIN}:${COAP_PORT}/system/time"

# Deferred / separate response
coap-client -m get "coap://${COAP_DOMAIN}:${COAP_PORT}/coap/async"

# CBOR-only contact form submission
echo -n '<CBOR-encoded map>' | coap-client -m post -t 60 \
  -f - "coap://${COAP_DOMAIN}:${COAP_PORT}/contact"
```

A Confirmable (`CON`) request to an unrecognized path receives a `4.04 Not Found` ACK; a Non-confirmable (`NON`) request to an unrecognized path receives no reply, per RFC 7252 §4.2.

## Tearing Down

```bash
./scripts/teardown.sh
```

Deletes every regional stack, then the global stack (which removes the DynamoDB Global Table and all of its replicas).
