using CoapDemo.Common.Protocol;
using CoapDemo.Common.Scheduling;

namespace CoapDemo.Common.Resources;

/// <summary>
/// <c>/coap/async</c> -- demonstrates CoAP's separate-response pattern (RFC 7252 §5.2.2).
/// A Confirmable request is immediately answered with an empty ACK (Code 0.00); the actual
/// content is delivered later, out of band, as a new Confirmable message carrying the same
/// Token but a fresh Message ID, sent via a Packet Source by the <c>AsyncResponder</c> Lambda
/// once the EventBridge Scheduler one-off rule fires. See design.md "Async Responses".
/// </summary>
public sealed class AsyncResource(IAsyncResponseScheduler scheduler, TimeSpan delay) : ResourceBase
{
    public override string Path => "/coap/async";
    public override string Title => "Asynchronous (Separate) Response Demo";
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Json, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override async Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        await scheduler.ScheduleAsync(ex.ClientAddress, ex.ClientPort, ex.Request.Token ?? "", ex.Request.MessageId, contentFormat, ex.Region, delay, ct);

        if (!ex.IsConfirmable)
            return new CoapResult { Code = CoapCodes.Content, Suppressed = true };

        // RFC 7252 §4.2/§5.2.2: empty ACK -- acknowledges the request without carrying content.
        // The Lambda (not UDP Gateway) is responsible for sending this; see design.md "Async Responses".
        return new CoapResult { Code = CoapCodes.Empty, Type = CoapTypes.Acknowledgement };
    }
}
