namespace CoapDemo.Common.Protocol;

/// <summary>One CoAP option as delivered/expected by the Proxylity "coap" destination formatter.</summary>
public sealed class CoapOption
{
    public int Number { get; set; }

    /// <summary>Base64-encoded raw option value bytes (empty string encodes a zero-length value).</summary>
    public string? Value { get; set; }

    public CoapOption() { }

    public CoapOption(int number, string? value)
    {
        Number = number;
        Value = value;
    }
}

/// <summary>
/// Structured CoAP request, decoded from the packet's <c>Data</c> field by Proxylity's
/// "coap" destination formatter. Mirrors the shape documented for UDP Gateway's CoAP JSON
/// formatter (Version/Type/Code/MessageId/Token/Method/Path/Options/Payload).
/// </summary>
public sealed class CoapRequest
{
    public int Version { get; set; } = 1;
    public int Type { get; set; }
    public int Code { get; set; }
    public int MessageId { get; set; }
    public string? Token { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public List<CoapOption>? Options { get; set; }
    public string? Payload { get; set; }
}

/// <summary>
/// Structured CoAP response, re-encoded into a binary CoAP packet by Proxylity's "coap"
/// destination formatter before delivery to the client.
/// </summary>
public sealed class CoapResponse
{
    public int Type { get; set; }
    public required string Code { get; set; }
    public int MessageId { get; set; }
    public string? Token { get; set; }
    public List<CoapOption>? Options { get; set; }
    public string? Payload { get; set; }
}
