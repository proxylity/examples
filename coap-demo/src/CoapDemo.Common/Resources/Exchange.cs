using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>Everything a resource handler needs to know about one inbound CoAP request.</summary>
public sealed class CoapExchange
{
    public required CoapRequest Request { get; init; }
    public required string ClientAddress { get; init; }
    public required int ClientPort { get; init; }
    public required string Region { get; init; }

    public IReadOnlyList<CoapOption> Options => Request.Options ?? [];

    public CoapOption? GetOption(int number) => Options.FirstOrDefault(o => o.Number == number);

    public bool HasOption(int number) => Options.Any(o => o.Number == number);

    public int? GetOptionUInt(int number)
    {
        var option = GetOption(number);
        return option is null ? null : (int)CoapCodec.Base64ToUInt(option.Value);
    }

    /// <summary>The request's declared body format (Content-Format option), if present.</summary>
    public int? RequestContentFormat => GetOptionUInt(CoapOptionNumbers.ContentFormat);

    /// <summary>The client's preferred response format (Accept option), if present.</summary>
    public int? Accept => GetOptionUInt(CoapOptionNumbers.Accept);

    /// <summary>The raw Observe option value (0 = register, 1 = deregister), if the option is present.</summary>
    public int? Observe => GetOptionUInt(CoapOptionNumbers.Observe);

    public byte[] PayloadBytes => string.IsNullOrEmpty(Request.Payload) ? [] : Convert.FromBase64String(Request.Payload);

    public bool IsConfirmable => Request.Type == CoapTypes.Confirmable;
}

/// <summary>Result of handling a CoAP request, ready to be turned into a <see cref="CoapResponse"/>.</summary>
public sealed class CoapResult
{
    public required string Code { get; init; }
    public List<CoapOption>? Options { get; init; }
    public byte[]? Payload { get; init; }

    /// <summary>Response message type override (CON/NON/ACK). Defaults to ACK (piggybacked) when null.</summary>
    public int? Type { get; init; }

    /// <summary>When true, no reply is sent at all for this message (e.g. the deferred half of a separate response).</summary>
    public bool Suppressed { get; init; }
}
