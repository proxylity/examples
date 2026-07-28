using System.Text;
using CoapDemo.Common.Cbor;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary><c>/demo/request</c> -- returns the structured CoAP request object exactly as the Lambda received it.</summary>
public sealed class RequestEchoResource : ResourceBase
{
    public override string Path => "/demo/request";
    public override string Title => "Request Introspection Demo";
    public override string? ResourceType => "demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var r = ex.Request;
        byte[] payload = contentFormat == Protocol.ContentFormats.Cbor
            ? CborHelpers.EncodeMap([
                ("method", r.Method ?? ""),
                ("path", r.Path ?? ""),
                ("messageId", r.MessageId),
                ("token", r.Token ?? ""),
                ("optionCount", r.Options?.Count ?? 0),
                ("clientAddress", ex.ClientAddress),
                ("clientPort", ex.ClientPort),
              ])
            : Encoding.UTF8.GetBytes(
                $"Method={r.Method} Path={r.Path} MessageId={r.MessageId} Token={r.Token} " +
                $"Options={r.Options?.Count ?? 0} From={ex.ClientAddress}:{ex.ClientPort}");

        return Task.FromResult(Ok(payload, contentFormat));
    }
}

/// <summary><c>/demo/region</c> -- returns the AWS region that handled this request (design.md "Multi-Region Deployment").</summary>
public sealed class RegionResource(string region) : ResourceBase
{
    public override string Path => "/demo/region";
    public override string Title => "Handling Region Demo";
    public override string? ResourceType => "demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var payload = contentFormat == Protocol.ContentFormats.Cbor
            ? CborHelpers.EncodeSingleFieldMap("region", region)
            : Encoding.UTF8.GetBytes(region);
        return Task.FromResult(Ok(payload, contentFormat));
    }
}

/// <summary><c>/demo/ping</c> -- simple echo response carrying a server-side timestamp.</summary>
public sealed class PingResource : ResourceBase
{
    public override string Path => "/demo/ping";
    public override string Title => "Ping Demo";
    public override string? ResourceType => "demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var payload = contentFormat == Protocol.ContentFormats.Cbor
            ? CborHelpers.EncodeSingleFieldMap("time", now)
            : Encoding.UTF8.GetBytes($"pong @ {now}");
        return Task.FromResult(Ok(payload, contentFormat));
    }
}
