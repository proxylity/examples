namespace CoapDemo.Common.Data;

/// <summary>CBOR-decoded body of a <c>POST /contact</c> request (see design.md "Contact Resource").</summary>
public sealed class ContactMessage
{
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
}

/// <summary>
/// Payload passed as EventBridge Scheduler target input for a one-off <c>/coap/async</c>
/// deferred response (see design.md "Async Responses").
/// </summary>
public sealed class AsyncResponseSchedule
{
    public required string ClientAddress { get; set; }
    public required int ClientPort { get; set; }
    public required string Token { get; set; }
    public required int MessageId { get; set; }
    public required int ContentFormat { get; set; }
    public required string EgressRegion { get; set; }
}
