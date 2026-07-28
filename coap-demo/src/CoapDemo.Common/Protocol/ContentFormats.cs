namespace CoapDemo.Common.Protocol;

/// <summary>
/// CoAP Content-Format identifiers (IANA "CoAP Content-Formats" registry) used by this demo.
/// Only the well-established, universally supported entries are used -- image/png in
/// particular is delivered as <see cref="OctetStream"/> since there is no single
/// widely-implemented Content-Format number for it across CoAP stacks.
/// </summary>
public static class ContentFormats
{
    public const int TextPlain = 0;
    public const int PngImage = 23;
    public const int LinkFormat = 40;
    public const int OctetStream = 42;
    public const int Json = 50;
    public const int Cbor = 60;
}
