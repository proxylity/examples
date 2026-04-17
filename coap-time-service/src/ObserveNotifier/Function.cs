using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

// Bootstrap — AOT-safe top-level entry point.
var instance = new Function();
var serializer = new SourceGeneratorLambdaJsonSerializer<JsonContext>();
await LambdaBootstrapBuilder.Create<JsonElement>(instance.FunctionHandler, serializer)
    .Build()
    .RunAsync();

/// <summary>
/// RFC 7641 Observe notifier — invoked once per minute by EventBridge Scheduler.
/// Scans the ObserveTable for active subscriptions and sends a NON CoAP 2.05 Content
/// notification to each subscriber carrying the current UTC time, the running Observe
/// sequence number (24-bit, Option 6), Content-Format=0 (Option 12), and Max-Age=60
/// (Option 14) so clients know when to re-register.
///
/// Subscription liveness: clients must re-register (GET /time, Observe=0) at least once
/// every 180 seconds. The state machine sets Expires = now + 180 on each registration;
/// this Lambda skips items whose Expires has passed. DynamoDB TTL provides an eventual
/// hard cleanup.
/// </summary>
public class Function(IAmazonDynamoDB ddb, IAmazonSimpleNotificationService sns)
{
    private readonly IAmazonDynamoDB _ddb = ddb;
    private readonly IAmazonSimpleNotificationService _sns = sns;

    private static readonly string TABLE_NAME = Environment.GetEnvironmentVariable(nameof(TABLE_NAME))
        ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set.");
    private static readonly string REPLY_TOPIC_ARN = Environment.GetEnvironmentVariable(nameof(REPLY_TOPIC_ARN))
        ?? throw new InvalidOperationException($"{nameof(REPLY_TOPIC_ARN)} environment variable is not set.");

    public Function() : this(new AmazonDynamoDBClient(), new AmazonSimpleNotificationServiceClient()) { }

    // ── Entry point ────────────────────────────────────────────────────────────

    public async Task FunctionHandler(JsonElement _, ILambdaContext context)
    {
        var now = DateTimeOffset.UtcNow;
        long nowEpoch = now.ToUnixTimeSeconds();
        string currentTime = now.ToString("O"); // ISO 8601 — matches Respond With Time format

        var records = await ScanActiveSubscriptionsAsync(nowEpoch, CancellationToken.None);
        context.Logger.LogInformation($"Sending observe notifications to {records.Count} subscriber(s).");

        await Task.WhenAll(records.Select(r => SendNotificationAsync(r, currentTime, context)));
    }

    // ── Subscription scan ──────────────────────────────────────────────────────

    private async Task<List<ObserveRecord>> ScanActiveSubscriptionsAsync(long nowEpoch, CancellationToken token)
    {
        var records = new List<ObserveRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new ScanRequest
            {
                TableName = TABLE_NAME,
                FilterExpression = "Expires > :now",
                ExpressionAttributeValues = new() { { ":now", new() { N = nowEpoch.ToString() } } },
                ExclusiveStartKey = lastKey is { Count: > 0 } ? lastKey : null
            };

            var response = await _ddb.ScanAsync(request, token);

            foreach (var item in response.Items)
            {
                if (!item.TryGetValue("Remote", out var remoteAttr)) continue;
                var remote = JsonSerializer.Deserialize(remoteAttr.S, JsonContext.Default.RemoteEndpoint);
                if (remote is null) continue;

                InnerInfo? inner = null;
                if (item.TryGetValue("Inner", out var innerAttr) && !string.IsNullOrEmpty(innerAttr.S))
                    inner = JsonSerializer.Deserialize(innerAttr.S, JsonContext.Default.InnerInfo);

                records.Add(new ObserveRecord(
                    ClientEndpoint: item["ClientEndpoint"].S,
                    Token:          item["Token"].S,
                    Remote:         remote,
                    Inner:          inner
                ));
            }

            lastKey = response.LastEvaluatedKey;
        }
        while (lastKey is { Count: > 0 });

        return records;
    }

    // ── Notification send ─────────────────────────────────────────────────────

    private async Task SendNotificationAsync(ObserveRecord record, string currentTime, ILambdaContext context)
    {
        // CoAP Observe sequence numbers are 24-bit (RFC 7641 §4.4). Unix epoch seconds
        // mod 2^24 satisfies the "fresher" ordering requirement (RFC 7641 §4.4) without
        // a persistent counter — 60 s always elapses between sends, so each value is
        // strictly greater than the previous within the 128-second comparison window.
        var observeSeq = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFF);
        var messageId  = Random.Shared.Next(1, 65536);

        // RFC 7252 §3.2 uint: minimal big-endian encoding (leading zero bytes omitted; 0 => empty).
        static string CoapUIntBase64(int value)
        {
            if (value == 0) return "";
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)value);
            return Convert.ToBase64String(((ReadOnlySpan<byte>)buf).TrimStart((byte)0));
        }

        // Build the CoAP NON (Type=1) 2.05 Content notification.
        // The Data string mirrors the JSON format that the Proxylity CoAP formatter
        // accepts for outbound packets — the same structure the state machine produces
        // for inbound request responses.
        var coapJson = JsonSerializer.Serialize(
            new CoapMessage(
                Type:      1,       // NON — no ACK expected; subscribers re-register to prove liveness
                Code:      "2.05",
                MessageId: messageId,
                Token:     record.Token,
                Options: [
                    new CoapOption(Number: 6,  Value: CoapUIntBase64(observeSeq)), // Observe (24-bit seq)
                    new CoapOption(Number: 12, Value: ""),                        // Content-Format = text/plain (0) — zero encodes to empty
                    new CoapOption(Number: 14, Value: CoapUIntBase64(60))          // Max-Age = 60 s
                ],
                Payload: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(currentTime))
            ),
            JsonContext.Default.CoapMessage);

        var envelope = JsonSerializer.Serialize(
            new ProxylityResponse(Messages: [
                new ProxylityReplyMessage(
                    Remote: new ProxylityRemote(
                        Address: record.Remote.Address,
                        Port:    record.Remote.Port,
                        PeerKey: record.Remote.PeerKey),
                    Inner: record.Inner,
                    Formatter: "coap",
                    Data: coapJson)
            ]),
            JsonContext.Default.ProxylityResponse);

        await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = REPLY_TOPIC_ARN,
            Message  = envelope
        }, CancellationToken.None);

        context.Logger.LogInformation(
            $"Observe notification sent: endpoint={record.ClientEndpoint} seq={observeSeq} msgId={messageId}");
    }
}

// ── Domain records ─────────────────────────────────────────────────────────────

/// <summary>Row from the ObserveTable — one active subscription.</summary>
public record ObserveRecord(string ClientEndpoint, string Token, RemoteEndpoint Remote, InnerInfo? Inner);

/// <summary>
/// Client endpoint stored in DDB as a JSON string using outbound field names
/// (Address / Port / PeerKey) so it can be passed directly to the SNS reply envelope.
/// </summary>
public record RemoteEndpoint(string Address, int Port, string PeerKey);

/// <summary>Inner tunnel addressing stored in DDB and used in the reply envelope (source/dest swapped).</summary>
public record InnerInfo(string SourceAddress, int SourcePort, string DestinationAddress, int DestinationPort, int Version, int Protocol);

/// <summary>One CoAP option — Number and base64-or-string Value.</summary>
public record CoapOption(int Number, string Value);

/// <summary>
/// Proxylity CoAP outbound message structure — serialises to the JSON that the
/// Proxylity CoAP formatter expects for outbound packets.
/// </summary>
public record CoapMessage(int Type, string Code, int MessageId, string Token,
    List<CoapOption> Options, string Payload);

/// <summary>Target endpoint for a Proxylity outbound reply.</summary>
public record ProxylityRemote(string Address, int Port, string PeerKey);

/// <summary>Single message in a Proxylity reply batch.</summary>
public record ProxylityReplyMessage(ProxylityRemote Remote, InnerInfo? Inner, string Formatter, string Data);

/// <summary>Envelope published to the Proxylity PacketSource SNS topic.</summary>
public record ProxylityResponse(List<ProxylityReplyMessage> Messages);

// ── AOT-safe JSON serialisation context ────────────────────────────────────────

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(RemoteEndpoint))]
[JsonSerializable(typeof(InnerInfo))]
[JsonSerializable(typeof(CoapOption))]
[JsonSerializable(typeof(List<CoapOption>))]
[JsonSerializable(typeof(CoapMessage))]
[JsonSerializable(typeof(ProxylityRemote))]
[JsonSerializable(typeof(ProxylityReplyMessage))]
[JsonSerializable(typeof(List<ProxylityReplyMessage>))]
[JsonSerializable(typeof(ProxylityResponse))]
public partial class JsonContext : JsonSerializerContext { }
