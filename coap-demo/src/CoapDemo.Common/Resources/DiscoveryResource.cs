using System.Text;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// Implements <c>GET /.well-known/core</c> (RFC 6690 CoRE Link Format resource discovery).
/// Every resource in the <see cref="ResourceRegistry"/> is advertised generically from its
/// declared metadata -- no resource-specific discovery logic is required.
/// </summary>
public sealed class DiscoveryResource(ResourceRegistry registry) : ResourceBase
{
    private readonly ResourceRegistry _registry = registry;

    public override string Path => "/.well-known/core";
    public override string Title => "Resource Discovery";
    public override string? ResourceType => "core.wk";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.LinkFormat];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var links = _registry.All
            .Where(r => r.Path != Path)
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .Select(BuildLink);

        var body = string.Join(",\n", links);
        return Task.FromResult(Ok(Encoding.UTF8.GetBytes(body), contentFormat));
    }

    private static string BuildLink(ICoapResource r)
    {
        // Deliberately omits the "title" attribute: with ~20 registered resources, including
        // human-readable titles pushes the encoded response past RFC 7252 §4.6's recommended
        // 1024-byte payload / 1152-byte message ceiling, which is what a default libcoap client
        // enforces for non-block-wise transfers (observed as "pdu too big" / malformed PDU
        // discards). rt/if/ct/obs are what machines actually need for discovery.
        var attrs = new List<string>();
        if (r.ResourceType is not null) attrs.Add($"rt=\"{r.ResourceType}\"");
        if (r.InterfaceDescription is not null) attrs.Add($"if=\"{r.InterfaceDescription}\"");
        if (r.ContentFormats.Count > 0) attrs.Add($"ct=\"{string.Join(" ", r.ContentFormats)}\"");
        if (r.Observable) attrs.Add("obs");
        return attrs.Count > 0 ? $"<{r.Path}>;{string.Join(";", attrs)}" : $"<{r.Path}>";
    }
}
