using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;

namespace AsyncResponder;

/// <summary>
/// Triggered by the <c>AsyncQueue</c> SQS queue (design.md "Async Responses"). Each message
/// body is a JSON-serialized <see cref="AsyncResponseSchedule"/> enqueued by RequestHandler
/// with a per-message <c>DelaySeconds</c> equal to the configured async delay. Delivers the
/// deferred CoAP response as a new Confirmable message via the Packet Source SNS topic --
/// the RFC 7252 §5.2.2 "separate response", correlated to the original request by Token
/// (a fresh Message ID is used, as the original has already been ACKed).
/// </summary>
public sealed class Function(IAmazonSimpleNotificationService sns)
{
    private readonly IAmazonSimpleNotificationService _sns = sns;
    private static readonly string ReplyTopicArn = Env("REPLY_TOPIC_ARN");

    public Function() : this(new AmazonSimpleNotificationServiceClient())
    {
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            var schedule = JsonSerializer.Deserialize(record.Body, CoapJsonContext.Default.AsyncResponseSchedule);
            if (schedule is null)
            {
                context.Logger.LogError($"Failed to deserialize SQS message {record.MessageId} -- skipping.");
                continue;
            }
            await SendResponseAsync(schedule, context);
        }
    }

    private async Task SendResponseAsync(AsyncResponseSchedule schedule, ILambdaContext context)
    {
        var messageId = Random.Shared.Next(1, 65536);
        var text = $"Deferred response for message {schedule.MessageId}, delivered asynchronously via a Packet Source.";

        var payload = schedule.ContentFormat switch
        {
            ContentFormats.Json => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["message"] = text }, CoapJsonContext.Default.DictionaryStringString),
            ContentFormats.Cbor => CoapDemo.Common.Cbor.CborHelpers.EncodeSingleFieldMap("message", text),
            _ => Encoding.UTF8.GetBytes(text),
        };

        var response = new CoapResponse
        {
            Type = CoapTypes.Confirmable,
            Code = CoapCodes.Content,
            MessageId = messageId,
            Token = schedule.Token,
            Options = [new CoapOption(CoapOptionNumbers.ContentFormat, CoapCodec.UIntToBase64(schedule.ContentFormat))],
            Payload = Convert.ToBase64String(payload),
        };

        var envelope = new OutboundEnvelope
        {
            Messages =
            [
                new OutboundMessage
                {
                    Remote = new OutboundRemote { Address = schedule.ClientAddress, Port = schedule.ClientPort },
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
                ["EgressRegion"] = new MessageAttributeValue { DataType = "String", StringValue = schedule.EgressRegion },
            },
        });

        context.Logger.LogInformation($"Sent deferred async response to {schedule.ClientAddress}:{schedule.ClientPort} token={schedule.Token}");
    }

    private static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
