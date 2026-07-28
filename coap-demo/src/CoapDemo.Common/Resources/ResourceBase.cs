using System.Text;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// Base class that handles the concerns every resource shares -- method checking and
/// content negotiation -- so individual resources only implement their domain logic.
/// Content negotiation is generic: a resource just declares <see cref="ContentFormats"/>
/// and this base class picks the best match for the client's Accept option (falling back
/// to the request's Content-Format, then to the resource's first declared format).
/// </summary>
public abstract class ResourceBase : ICoapResource
{
    public abstract string Path { get; }
    public abstract string Title { get; }
    public virtual string? ResourceType => null;
    public virtual string? InterfaceDescription => null;
    public abstract IReadOnlyList<int> ContentFormats { get; }
    public virtual bool Observable => false;
    public abstract IReadOnlyList<int> AllowedMethods { get; }

    public async Task<CoapResult> HandleAsync(CoapExchange ex, CancellationToken ct)
    {
        if (!AllowedMethods.Contains(ex.Request.Code))
            return Problem(CoapCodes.MethodNotAllowed, "Method Not Allowed");

        var format = NegotiateContentFormat(ex);
        if (format is null)
            return Problem(CoapCodes.NotAcceptable, "Not Acceptable");

        return await ExecuteAsync(ex, format.Value, ct);
    }

    /// <summary>Implements the resource's domain logic for the negotiated <paramref name="contentFormat"/>.</summary>
    protected abstract Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct);

    private int? NegotiateContentFormat(CoapExchange ex)
    {
        var requested = ex.Accept ?? ex.RequestContentFormat;
        if (requested is null) return ContentFormats.Count > 0 ? ContentFormats[0] : null;
        return ContentFormats.Contains(requested.Value) ? requested : null;
    }

    protected static CoapResult Ok(byte[] payload, int contentFormat, List<CoapOption>? extraOptions = null)
    {
        var options = new List<CoapOption> { new(CoapOptionNumbers.ContentFormat, CoapCodec.UIntToBase64(contentFormat)) };
        if (extraOptions is not null) options.AddRange(extraOptions);
        return new CoapResult { Code = CoapCodes.Content, Options = options, Payload = payload };
    }

    protected static CoapResult Problem(string code, string message)
        => new() { Code = code, Payload = Encoding.UTF8.GetBytes(message) };
}
