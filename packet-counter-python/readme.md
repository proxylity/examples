## Packet Counter

This example demonstrates a packet counting UDP endpoint implemented with Proxylity UDP Gateway and AWS Lambda.  Clients sending packets to the endpoint will receive responses containing the quantity of packets received per batch. If a single packet is sent to the endpoint, a single response will be returned with string value `1` as the content.  At higher rates the number in the return packet will increase and it is likely that more than one response will be received due to the multi-region/multi-AZ infrastructure employed by UDP Gateway.

This example demonstrates:

* Using the Proxylity listener custom resource type for CloudFormation.
* Handling batches of UDP packet in AWS Lambda.
* Selectively/conditionally generating responses to packets.

## System Diagram

```mermaid
graph LR

subgraph proxylity
listener
destination
end

subgraph customer aws
lambda
end

clients<-->listener<-->destination<-->lambda
```

## Deploying

> **NOTE**: The instructions below assume the `aws` CLI, `jq`, `sam` and `ncat` are available on your Linux system.\
> Also, it is assumed you have an S3 Bucket on AWS for `sam` to work with.\
> **Python** Version Requirement for this example is `3.11`.

To setup artifact for deployment:

```bash
sam build --template-file ./packet-counter.template.json
```

To deploy the template:

```bash
sam deploy \
    --stack-name packet-counter-example \
    --capabilities CAPABILITY_IAM \
    --region us-west-2 \
    --s3-bucket <bucket-name>
```

Once deployed, the endpoint can be tested with `ncat` and the endpoint information provided in the outputs of the stack. To get the ouputs from the stack and store the salient values in environment variables:

```bash
aws cloudformation describe-stacks \
  --stack-name packet-counter-example \
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > outputs.json 

export PACKET_COUNTER_DOMAIN=$(jq -r ".[]|select(.OutputKey==\"Domain\")|.OutputValue" outputs.json)
export PACKET_COUNTER_PORT=$(jq -r ".[]|select(.OutputKey==\"Port\")|.OutputValue" outputs.json)
```

Then to send a single test packet and output the response:

```bash
echo -e Response: $((echo "test" && sleep 2) | ncat -u ${PACKET_COUNTER_DOMAIN} ${PACKET_COUNTER_PORT} -w2)
```

That should elicite output of "Response: 1".

To remove the example stack:
```bash
aws cloudformation delete-stack --stack-name packet-counter-example --region us-west-2
```

## Lambda Implementation

Proxylity forwards packet data to Lambda in JSON format, per the documented [JSON Schema](https://www.proxylity.com/docs/destinations/json-packet-format.html). In this lambda we're interested in a subset of the properties:

```jsonc
{
  "Messages": [
    { 
      "Tag": "",
      "Remote": {
        "IpAddress": "",
        "Port": 0
      },
      // ...
      "Data": "<Base64>"
    },
    // ...
  ]
}
```

The output of the Lambda instructs Proxylity what responses, if any, to send in for each input packet. It's okay to not include all `Tag` values in the output and produce few response, or even none:

```jsonc
{
  "Replies": [
    { 
      "Tag": "",
      // ...
      "Data": "<Base64>"
    },
    // ...
  ]
}
```

The first step in the code is to count the number of packets in the batch that come from the same IP:

```python
# Count packets per source IP
counts: Dict[str, int] = {}
for msg in messages:
    ip = msg.Remote.IpAddress
    counts[ip] = counts.get(ip, 0) + 1
```

The second step is to generate the replies (outbound/response packets), but only send one response per IP.  The code uses a helper function that keeps track of which IPs already have a response by clearing the entry in the map of counts and base64 encoding the response data:

```python
def get_and_clear(m: Dict[str, int], key: str) -> Optional[str]:
    value = m.get(key, 0)
    if value == 0:
        return None
    m[key] = 0
    encoded = base64.b64encode(f"{value}\n".encode()).decode()
    return encoded
```

The list of response packets is then generated via looping over messages and appending a response packet to `replies`:

```python
replies: List[OutboundPacket] = []
for msg in messages:
    data = get_and_clear(counts, msg.Remote.IpAddress)
    if data is None:
        continue
    replies.append(OutboundPacket(
        GeneratedAt=datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
        Tag=msg.Tag,
        Data=data
    ))
```

And finally, return object with the `Replies` property to Proxylity:

```python
return {
    'Replies': [
        {
            'GeneratedAt': reply.GeneratedAt,
            'Tag': reply.Tag,
            'Data': reply.Data
        }
        for reply in replies
    ]
}
```
