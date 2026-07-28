namespace CoapDemo.Common.Resources;

/// <summary>A single routable CoAP resource, discoverable via <c>/.well-known/core</c>.</summary>
public interface ICoapResource
{
    /// <summary>The absolute URI path this resource is mounted at, e.g. "/system/health".</summary>
    string Path { get; }

    /// <summary>Human-readable title, advertised in CoRE Link Format discovery.</summary>
    string Title { get; }

    /// <summary>CoRE Link Format "rt" (resource type) attribute, if any.</summary>
    string? ResourceType { get; }

    /// <summary>CoRE Link Format "if" (interface description) attribute, if any.</summary>
    string? InterfaceDescription { get; }

    /// <summary>Content-Format identifiers this resource can produce/consume.</summary>
    IReadOnlyList<int> ContentFormats { get; }

    /// <summary>Whether this resource supports RFC 7641 Observe.</summary>
    bool Observable { get; }

    /// <summary>CoAP method codes (see <see cref="Protocol.CoapMethods"/>) this resource accepts.</summary>
    IReadOnlyList<int> AllowedMethods { get; }

    Task<CoapResult> HandleAsync(CoapExchange exchange, CancellationToken ct);
}
