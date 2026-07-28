using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// Base class for RFC 7641 Observe-capable resources. Registration/deregistration is
/// entirely generic -- individual resources only supply how to render their current value.
/// </summary>
public abstract class ObservableResourceBase(ObserveRegistry registry, int observeTtlSeconds) : ResourceBase
{
    protected readonly ObserveRegistry Registry = registry;
    protected readonly int ObserveTtlSeconds = observeTtlSeconds;

    public override bool Observable => true;

    /// <summary>Renders the resource's current value as bytes in the given (already-negotiated) content format.</summary>
    protected abstract Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct);

    protected override async Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var token = ex.Request.Token ?? "";
        var options = new List<CoapOption>();

        var payload = await RenderCurrentValueAsync(contentFormat, ct);

        if (ex.Observe == 0)
        {
            await Registry.RegisterAsync(Path, ex.ClientAddress, ex.ClientPort, token, contentFormat, ex.Region, ObserveTtlSeconds, ct);
            options.Add(new CoapOption(CoapOptionNumbers.Observe, CoapCodec.UIntToBase64(0)));
            options.Add(new CoapOption(CoapOptionNumbers.MaxAge, CoapCodec.UIntToBase64(ObserveTtlSeconds)));
        }
        else if (ex.Observe == 1)
        {
            await Registry.DeregisterAsync(Path, ex.ClientAddress, ex.ClientPort, token, ct);
        }

        return Ok(payload, contentFormat, options);
    }
}

/// <summary>
/// <c>/coap/observers</c> -- reports the number of clients currently observing this same
/// resource, and (via <c>GlobalNotifier</c>) pushes a notification whenever that count changes.
/// See design.md "Observers Demo".
/// </summary>
public sealed class ObserversResource(ObserveRegistry registry, int observeTtlSeconds)
    : ObservableResourceBase(registry, observeTtlSeconds)
{
    public override string Path => "/coap/observers";
    public override string Title => "Observers Count Demo";
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Json, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override async Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct)
    {
        var count = await Registry.CountAsync(Path, ct);
        return ContentPayloads.EncodeNumber(contentFormat, "observers", count);
    }
}
