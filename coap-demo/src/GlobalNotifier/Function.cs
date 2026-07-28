using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;
using CoapDemo.Common.Resources;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GlobalNotifier;

/// <summary>
/// Fires on every change to this region's replica of the Global table's DynamoDB Stream.
/// Two independent event-driven notification flows share this one function (design.md
/// "Observers Demo" and "System Resource Updates"):
///
///  - Subscription lifecycle (INSERT/REMOVE of an "OBS#/coap/observers" item) recomputes the
///    global observer count and pushes it to every subscriber this region owns.
///  - Value changes (MODIFY of a "SYS#{path}" item, written by RegionalAggregator) push the
///    new value to every subscriber this region owns, rate-limited for the high-frequency
///    metrics/time resources.
///
/// In both cases only subscribers whose LastWriteRegion matches this Lambda's own region are
/// notified -- each region owns and notifies only the clients that registered with it, even
/// though the underlying state is globally consistent (design.md "Observe Registry").
/// </summary>
public sealed class Function(IAmazonDynamoDB ddb, IAmazonSimpleNotificationService sns)
{
    private static readonly string Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown";
    private static readonly string ReplyTopicArn = Env("REPLY_TOPIC_ARN");
    private static readonly int RateLimitSeconds = int.Parse(Environment.GetEnvironmentVariable("BROADCAST_RATE_LIMIT_SECONDS") ?? "60");

    private readonly ObserveRegistry _observeRegistry = new(ddb, Env("GLOBAL_TABLE_NAME"));
    private readonly SystemStateStore _systemState = new(ddb, Env("GLOBAL_TABLE_NAME"));
    private readonly RegionalMetricsStore _regionalMetrics = new(ddb, Env("REGIONAL_TABLE_NAME"));
    private readonly IAmazonSimpleNotificationService _sns = sns;

    public Function() : this(new AmazonDynamoDBClient(), new AmazonSimpleNotificationServiceClient())
    {
    }

    public async Task FunctionHandler(DynamoDBEvent evnt, ILambdaContext context)
    {
        foreach (var record in evnt.Records)
            await ProcessRecordAsync(record, context);
    }

    private async Task ProcessRecordAsync(DynamoDBEvent.DynamodbStreamRecord record, ILambdaContext context)
    {
        var pk = record.Dynamodb.Keys.TryGetValue("PK", out var pkAttr) ? pkAttr.S : null;
        if (pk is null) return;

        if (pk.StartsWith("OBS#/coap/observers", StringComparison.Ordinal) && record.EventName is "INSERT" or "REMOVE")
        {
            await NotifyObserversCountChangedAsync(context);
            return;
        }

        if (pk.StartsWith("SYS#", StringComparison.Ordinal) && record.EventName is "MODIFY" or "INSERT")
        {
            var image = record.Dynamodb.NewImage;
            if (!image.TryGetValue("Path", out var pathAttr)) return;
            await NotifySystemValueChangedAsync(pathAttr.S, context);
        }
    }

    private async Task NotifyObserversCountChangedAsync(ILambdaContext context)
    {
        const string path = "/coap/observers";
        var count = await _observeRegistry.CountAsync(path);
        var subscribers = (await _observeRegistry.ListAsync(path)).Where(s => s.LastWriteRegion == Region).ToList();

        context.Logger.LogInformation($"{path}: count={count}, notifying {subscribers.Count} region-owned subscriber(s).");

        await Task.WhenAll(subscribers.Select(s =>
            PublishNotificationAsync(s, ContentPayloads.EncodeNumber(s.ContentFormat, "observers", count))));
    }

    private async Task NotifySystemValueChangedAsync(string path, ILambdaContext context)
    {
        // Health/version change essentially never (deploy-time only); metrics/time change on
        // every request, so those two are rate-limited per design.md "Metrics Broadcast Rate Limiting".
        var rateLimited = path is SystemStateStore.MetricsPath or SystemStateStore.TimePath;
        if (rateLimited && !await _regionalMetrics.TryAcquireBroadcastAsync(path, RateLimitSeconds))
            return;

        var subscribers = (await _observeRegistry.ListAsync(path)).Where(s => s.LastWriteRegion == Region).ToList();
        if (subscribers.Count == 0) return;

        var state = await _systemState.GetAsync(path);
        context.Logger.LogInformation($"{path}: notifying {subscribers.Count} region-owned subscriber(s).");

        await Task.WhenAll(subscribers.Select(s => PublishNotificationAsync(s, RenderValue(path, state, s.ContentFormat))));
    }

    private static byte[] RenderValue(string path, SystemState? state, int contentFormat) => path switch
    {
        SystemStateStore.HealthPath => ContentPayloads.Encode(contentFormat, "status", state?.Status ?? "unknown"),
        SystemStateStore.VersionPath => ContentPayloads.Encode(contentFormat, "version", state?.Version ?? "unknown"),
        SystemStateStore.MetricsPath => ContentPayloads.EncodeNumber(contentFormat, "requests", state?.RequestCount ?? 0),
        SystemStateStore.TimePath => ContentPayloads.Encode(contentFormat, "time", state?.LastRequestTime ?? DateTimeOffset.UtcNow.ToString("O")),
        _ => [],
    };

    private async Task PublishNotificationAsync(ObserveSubscription subscription, byte[] payload)
    {
        var response = new CoapResponse
        {
            Type = CoapTypes.NonConfirmable,
            Code = CoapCodes.Content,
            MessageId = Random.Shared.Next(1, 65536),
            Token = subscription.Token,
            Options =
            [
                // RFC 7641 §4.4: 24-bit sequence derived from the Unix epoch; always increasing across sends.
                new CoapOption(CoapOptionNumbers.Observe, CoapCodec.UIntToBase64(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFF)),
                new CoapOption(CoapOptionNumbers.ContentFormat, CoapCodec.UIntToBase64(subscription.ContentFormat)),
                new CoapOption(CoapOptionNumbers.MaxAge, CoapCodec.UIntToBase64(RateLimitSeconds)),
            ],
            Payload = Convert.ToBase64String(payload),
        };

        var envelope = new OutboundEnvelope
        {
            Messages =
            [
                new OutboundMessage
                {
                    Remote = new OutboundRemote { Address = subscription.Address, Port = subscription.Port },
                    Formatter = "coap",
                    Data = JsonSerializer.Serialize(response, CoapJsonContext.Default.CoapResponse),
                }
            ]
        };

        await _sns.PublishAsync(new PublishRequest
        {
            TopicArn = ReplyTopicArn,
            Message = JsonSerializer.Serialize(envelope, CoapJsonContext.Default.OutboundEnvelope),
            MessageAttributes = new()
            {
                ["EgressRegion"] = new MessageAttributeValue { DataType = "String", StringValue = subscription.LastWriteRegion },
            },
        });
    }

    private static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
