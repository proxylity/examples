using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using CoapDemo.Common.Cbor;
using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// <c>GET /contact</c> returns usage instructions. <c>POST /contact</c> accepts a
/// CBOR-encoded contact message, validates it, and fires an EventBridge event carrying
/// the same fields as a JSON object. See design.md "Contact Resource" and "Input
/// Validation". Only <c>application/cbor</c> is accepted for POST; any other (or missing)
/// Content-Format is rejected with 4.15 Unsupported Content-Format.
/// </summary>
public sealed partial class ContactResource(IAmazonEventBridge eventBridge, string eventBusName) : ResourceBase
{
    private const int MaxShortFieldLength = 200;
    private const int MaxBodyLength = 400;

    public override string Path => "/contact";
    public override string Title => "Contact Form";
    public override string? ResourceType => "contact";
    private const string GetResponse =
        "POST /contact with Content-Format: application/cbor (60)\n" +
        "CBOR map with required text (major type 3) fields:\n" +
        "  name    your name (max 200 chars)\n" +
        "  email   your email address (max 200 chars)\n" +
        "  subject message subject (max 200 chars)\n" +
        "  body    message body (max 400 chars)\n";

    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get, CoapMethods.Post];

    protected override async Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        if (ex.Request.Code == CoapMethods.Get)
            return Ok(Encoding.UTF8.GetBytes(GetResponse), Protocol.ContentFormats.TextPlain);

        // The base class already restricts negotiated formats to Cbor via Accept, but a
        // POST body's format is declared by Content-Format, not Accept -- check explicitly.
        if (ex.RequestContentFormat != Protocol.ContentFormats.Cbor)
            return Problem(CoapCodes.UnsupportedContentFormat, "Only application/cbor is accepted on /contact.");

        Dictionary<string, string> fields;
        try
        {
            fields = CborHelpers.ReadFlatTextMap(ex.PayloadBytes);
        }
        catch (Exception)
        {
            return Problem(CoapCodes.BadRequest, "Malformed CBOR body.");
        }

        var validationError = Validate(fields, out var message);
        if (validationError is not null)
            return Problem(CoapCodes.BadRequest, validationError);

        await eventBridge.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = eventBusName,
                    Source = "coap-demo",
                    DetailType = "ContactSubmitted",
                    Detail = JsonSerializer.Serialize(message, CoapJsonContext.Default.ContactMessage),
                }
            ]
        }, ct);

        return new CoapResult { Code = CoapCodes.Created };
    }

    private static string? Validate(Dictionary<string, string> fields, out ContactMessage message)
    {
        message = default!;
        foreach (var required in new[] { "name", "email", "subject", "body" })
        {
            if (!fields.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
                return $"Missing required field: {required}";
        }

        var (name, email, subject, body) = (fields["name"], fields["email"], fields["subject"], fields["body"]);

        if (name.Length > MaxShortFieldLength) return "name exceeds maximum length.";
        if (email.Length > MaxShortFieldLength) return "email exceeds maximum length.";
        if (subject.Length > MaxShortFieldLength) return "subject exceeds maximum length.";
        if (body.Length > MaxBodyLength) return "body exceeds maximum length.";
        if (!EmailRegex().IsMatch(email)) return "email is not syntactically valid.";

        message = new ContactMessage { Name = name, Email = email, Subject = subject, Body = body };
        return null;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
