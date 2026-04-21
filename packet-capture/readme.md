# Packet Capture

This example captures UDP packets arriving over **plain UDP** and optionally **WireGuard** and displays them live in a browser. There is no persistent packet storage — packets flow from the Proxylity listener through an SQS queue and a Lambda worker straight into an [AppSync Events](https://docs.aws.amazon.com/appsync/latest/eventapi/event-api-welcome.html) channel, which pushes them in real time to any connected browser.

Use this project for quick, ad-hoc packet capture when local capture isn't convenient or even possible. A similar pattern can be applied to any Proxylity Listener using the SQS queue as a secondary destination to give live and real-time obvservability of messages as they arrive without disturbing the flow to the primary destination(s).

## System Diagram

![Packet Capture Architecture](./packet-capture.svg)

## Features

- **Pure Serverless** — Zero cost when not in use, quick to deploy and quick to tear down.
- **Live, zero-storage capture** — packets are never written to a database; the only transient storage is the SQS queue.
- **Dual-protocol ingress** — plain UDP and WireGuard listeners both funnel into the same pipeline.
- **AppSync Events fan-out** — the browser subscribes directly to the AppSync Events channel over a WebSocket; any number of browser tabs see the same stream simultaneously.
- **Self-contained UI** — the HTML page is rendered by a Lambda function behind a public Lambda URL, so there is nothing to host or deploy separately.
- **Newest packets on top** — the UI prepends each incoming row so you always see the most recent activity.

## How It Works

1. A UDP client sends packets to the Proxylity listener endpoint.
2. Proxylity delivers each packet as an individual JSON message to the SQS standard queue. The SQS event source mapping batches up to 10 of these messages per Lambda invocation.
3. SQS triggers `PacketProcessor`, which for each message:
   - Deserialises the Proxylity envelope (`Remote.IpAddress`, `Remote.Port`, base-64 `Data`)
   - Dissects the packet payload into protocol layers using PacketDotNet
   - `POST`s the resulting `PacketEvent` to the AppSync Events HTTP endpoint (`/event`)
4. Any browser tab subscribed to `/packets/capture` receives the event via WebSocket and prepends a new row to the table.
5. Opening the `LiveUiUrl` stack output in a browser loads the HTML, which immediately connects to AppSync Events and begins listening.

## Deploying

> **Prerequisites**: AWS CLI, SAM CLI, and the .NET 10 SDK. To enable WireGuard capture, `wireguard-tools` must also be available. You must be **subscribed to Proxylity UDP Gateway** at the Pro or Enterprise level (packet sourcing is not available on Free plans).

### 1 — Generate a WireGuard key pair (optional)

Skip this step if you only need plain-UDP capture.

```bash
wg genkey | tee client.key | wg pubkey > client.pub
```

### 2 — Build and deploy

Plain-UDP only:

```bash
sam build && sam deploy --guided
```

With WireGuard capture enabled, pass your client public key:

```bash
sam build && sam deploy --guided \
  --parameter-overrides WireGuardClientPublicKey=$(cat client.pub)
```

### 3 — Retrieve stack outputs

```bash
aws cloudformation describe-stacks \
  --stack-name packet-capture \
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > outputs.json

export UDP_ENDPOINT=$(jq  -r '.[]|select(.OutputKey=="UdpEndpoint")             |.OutputValue' outputs.json)
export UI_URL=$(jq        -r '.[]|select(.OutputKey=="LiveUiUrl")               |.OutputValue' outputs.json)
export WG_ENDPOINT=$(jq   -r '.[]|select(.OutputKey=="WireGuardEndpoint")       |.OutputValue' outputs.json)
export WG_SERVER_KEY=$(jq -r '.[]|select(.OutputKey=="WireGuardServerPublicKey")|.OutputValue' outputs.json)
```

### 4 — Configure a WireGuard tunnel (optional)

Skip this step if you deployed without `WireGuardClientPublicKey`.

```bash
sudo tee /etc/wireguard/capture.conf <<EOF
[Interface]
PrivateKey = $(cat client.key)
Address = 10.10.10.10/32

[Peer]
PublicKey = ${WG_SERVER_KEY}
Endpoint = ${WG_ENDPOINT}
AllowedIPs = 10.10.10.11/32
PersistentKeepalive = 25
EOF
sudo wg-quick up capture
```

> **Note**: Using `AllowedIPs = 10.10.10.11/32` routes only packets *destined for* `10.10.10.11` through the WireGuard tunnel, leaving all other traffic — including the plain-UDP endpoint's public IP — on the normal network path. Both endpoints can be used simultaneously.

### 5 — Open the live UI

```bash
echo "Open this URL in your browser: ${UI_URL}"
```

### 6 — Send test packets

Plain UDP:

```bash
for i in $(seq 1 10); do
  echo "hello packet $i" | ncat -u ${UDP_ENDPOINT%:*} ${UDP_ENDPOINT#*:}
done
```

Over WireGuard (tunnel must be **up**, send to any port on `10.10.10.11`):

```bash
for i in $(seq 1 10); do
  echo "hello wg packet $i" | ncat -u 10.10.10.11 4321
done
```

Packets appear in the browser within a second or two.

### Removing the stack

```bash
sudo wg-quick down capture   # if WireGuard was enabled
sam delete
```

## Stack Outputs

| Output | Description |
|---|---|
| `UdpEndpoint` | Plain-UDP capture endpoint (`host:port`) |
| `WireGuardEndpoint` | WireGuard capture endpoint — only present when `WireGuardClientPublicKey` is set |
| `WireGuardServerPublicKey` | Server public key to configure in the WireGuard client peer |
| `LiveUiUrl` | Browser URL for the live capture UI |
