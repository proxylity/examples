using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Scheduling;

/// <summary>
/// Enqueues an <see cref="AsyncResponseSchedule"/> onto an SQS standard queue with a
/// per-message <c>DelaySeconds</c> equal to the configured async delay. SQS delivers the
/// message to <c>AsyncResponder</c> once the delay elapses, implementing the RFC 7252 §5.2.2
/// separate-response pattern without requiring EventBridge Scheduler's 1-minute minimum.
/// </summary>
public sealed class SqsAsyncResponseScheduler(IAmazonSQS sqs, string queueUrl) : IAsyncResponseScheduler
{
    private readonly IAmazonSQS _sqs = sqs;
    private readonly string _queueUrl = queueUrl;

    public Task ScheduleAsync(string clientAddress, int clientPort, string token, int messageId, int contentFormat, string egressRegion, TimeSpan delay, CancellationToken ct = default)
    {
        var schedule = new AsyncResponseSchedule
        {
            ClientAddress = clientAddress,
            ClientPort = clientPort,
            Token = token,
            MessageId = messageId,
            ContentFormat = contentFormat,
            EgressRegion = egressRegion,
        };

        return _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = JsonSerializer.Serialize(schedule, CoapJsonContext.Default.AsyncResponseSchedule),
            DelaySeconds = (int)Math.Clamp(delay.TotalSeconds, 0, 900),
        }, ct);
    }
}
