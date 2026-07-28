namespace CoapDemo.Common.Protocol;

/// <summary>Source/target endpoint for a UDP Gateway packet (plain UDP -- no WireGuard tunnel).</summary>
public sealed class ProxylityRemote
{
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>One inbound packet delivered to a Lambda Destination.</summary>
public sealed class RequestPacket
{
    public required string Tag { get; set; }
    public required ProxylityRemote Remote { get; set; }

    /// <summary>Stringified JSON of a <see cref="CoapRequest"/>, produced by the "coap" formatter.</summary>
    public required string Data { get; set; }
}

/// <summary>Envelope UDP Gateway sends to a Lambda Destination.</summary>
public sealed class ProxylityLambdaRequest
{
    public RequestPacket[]? Messages { get; set; }
}

/// <summary>One reply, correlated back to its request by <see cref="Tag"/>.</summary>
public sealed class ResponsePacket
{
    public required string Tag { get; set; }

    /// <summary>Stringified JSON of a <see cref="CoapResponse"/>. Omit to send no reply for that message.</summary>
    public string? Data { get; set; }
}

/// <summary>Envelope a Lambda Destination returns to UDP Gateway.</summary>
public sealed class ProxylityLambdaResponse
{
    public required List<ResponsePacket> Replies { get; set; }
}

/// <summary>Target endpoint for an unsolicited outbound packet sent via a Packet Source.</summary>
public sealed class OutboundRemote
{
    public required string Address { get; set; }
    public required int Port { get; set; }
}

/// <summary>One outbound message published to a Packet Source topic.</summary>
public sealed class OutboundMessage
{
    public required OutboundRemote Remote { get; set; }
    public required string Formatter { get; set; }

    /// <summary>Stringified JSON of a <see cref="CoapResponse"/>.</summary>
    public required string Data { get; set; }
}

/// <summary>Envelope published to a Proxylity Packet Source SNS topic.</summary>
public sealed class OutboundEnvelope
{
    public required List<OutboundMessage> Messages { get; set; }
}
