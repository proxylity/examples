namespace CoapDemo.Common.Scheduling;

/// <summary>
/// Schedules a one-off deferred CoAP response, per design.md "Async Responses": rather than
/// holding a Lambda invocation open (and .NET Lambdas cannot stream responses), the request
/// handler schedules a one-time EventBridge Scheduler rule that later invokes a dedicated
/// responder function, which delivers the actual response via a Packet Source.
/// </summary>
public interface IAsyncResponseScheduler
{
    Task ScheduleAsync(string clientAddress, int clientPort, string token, int messageId, int contentFormat, string egressRegion, TimeSpan delay, CancellationToken ct = default);
}
