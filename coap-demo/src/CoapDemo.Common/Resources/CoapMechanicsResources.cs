using System.Text;
using System.Text.Json;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// <c>/coap/con</c> and <c>/coap/non</c> -- returns the same static message, but as a
/// Confirmable (<see cref="CoapTypes.Confirmable"/>) or Non-confirmable
/// (<see cref="CoapTypes.NonConfirmable"/>) message respectively, demonstrating the two
/// CoAP reliability modes (RFC 7252 §2.1, §4.2, §4.3). A client library will retransmit
/// nothing further for the NON case, but must ACK the CON reply.
/// </summary>
public sealed class ConfirmabilityDemoResource(string path, string title, int responseType) : ResourceBase
{
    public override string Path => path;
    public override string Title => title;
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Json, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var mode = responseType == CoapTypes.Confirmable ? "Confirmable" : "Non-confirmable";
        var text = $"This is a {mode} response from {Path}.";
        var payload = ContentPayloads.Encode(contentFormat, "message", text);
        var result = Ok(payload, contentFormat);
        return Task.FromResult(new CoapResult { Code = result.Code, Options = result.Options, Payload = result.Payload, Type = responseType });
    }
}

/// <summary><c>/coap/binary</c> -- returns a small static PNG image to demonstrate binary payload delivery.</summary>
public sealed class BinaryResource : ResourceBase
{
    // A PNG image, perfectly chosen.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAMAAAAoLQ9TAAAAAXNSR0IArs4c6QAAAARnQU1BAACx" +
        "jwv8YQUAAACfUExURQAAAAEBASsrKzY2NjQ0NDMzMzIyMicnJ8XFxfLy8vHx8fPz89ra2omJidTU" +
        "1P///9nZ2bGxsba2tre3t8vLy/7+/oWFhdLS0qmpqbCwsM/Pz62trZaWltjY2KioqJWVlaKiolNT" +
        "U3h4eOTk5K+vr/v7+/39/SYmJs3NzZ6enpmZmWxsbAMDA6urq0hISNPT066urnZ2dpeXl2JiYgQE" +
        "BCzIJbIAAAAJcEhZcwAADr8AAA6/ATgFUyQAAAAZdEVYdFNvZnR3YXJlAFBhaW50Lk5FVCA1LjEu" +
        "MTITAUd0AAAAuGVYSWZJSSoACAAAAAUAGgEFAAEAAABKAAAAGwEFAAEAAABSAAAAKAEDAAEAAAAC" +
        "AAAAMQECABEAAABaAAAAaYcEAAEAAABsAAAAAAAAAKZ2AQDoAwAApnYBAOgDAABQYWludC5ORVQg" +
        "NS4xLjEyAAADAACQBwAEAAAAMDIzMAGgAwABAAAAAQAAAAWgBAABAAAAlgAAAAAAAAACAAEAAgAE" +
        "AAAAUjk4AAIABwAEAAAAMDEwMAAAAAC8FLx49dea3QAAAHpJREFUKFNjYMAEjOgCDEzMLKysbOwI" +
        "OQ5OLm5ubi4eXpgKPn4BQSFhEVFRMaiAOL8EiJLkl4ILSIMoGX5ZuICcvIKikjK/ClyAT5VfjZ9f" +
        "RB0uIKQhoqmlzcCgAxPQZdADMWBO1Oc3gEuCAR/cNCgwNDJG4ZtAMQ4AAM4MB76IPy3qAAAAAElF" +
        "TkSuQmCC"
        );

    public override string Path => "/coap/binary";
    public override string Title => "Binary Payload Demo";
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.PngImage];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
        => Task.FromResult(Ok(PngBytes, contentFormat));
}

/// <summary>
/// <c>/coap/large</c> -- returns a payload large enough to require RFC 7959 Block2
/// block-wise transfer. The full representation is generated once per negotiated content
/// format, then sliced according to the client's Block2 option (or block 0 at the server's
/// preferred size if the client did not send one).
/// </summary>
public sealed class LargeResource : ResourceBase
{
    private const int ServerPreferredSzx = 6; // 1024-byte blocks

    public override string Path => "/coap/large";
    public override string Title => "Large Payload Demo (Block2)";
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Json, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var full = BuildFullRepresentation(contentFormat);

        var (requestedNum, _, requestedSzx, _) = ex.HasOption(CoapOptionNumbers.Block2)
            ? CoapCodec.DecodeBlockOption(ex.GetOption(CoapOptionNumbers.Block2)!.Value)
            : (0, false, ServerPreferredSzx, 0);

        var szx = Math.Min(requestedSzx, ServerPreferredSzx);
        var blockSize = CoapCodec.SzxToBlockSize(szx);
        var start = requestedNum * blockSize;

        if (start >= full.Length)
            return Task.FromResult(Problem(CoapCodes.BadOption, "Block number beyond end of representation"));

        var length = Math.Min(blockSize, full.Length - start);
        var slice = full.AsSpan(start, length).ToArray();
        var more = start + length < full.Length;

        var options = new List<CoapOption>
        {
            new(CoapOptionNumbers.Block2, CoapCodec.EncodeBlockOption(requestedNum, more, szx)),
            new(CoapOptionNumbers.Size2, CoapCodec.UIntToBase64(full.Length)),
        };
        return Task.FromResult(Ok(slice, contentFormat, options));
    }

    private static byte[] BuildFullRepresentation(int contentFormat)
    {
        const string sentence = "Proxylity UDP Gateway turns raw UDP packets into structured application events. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 40)); // ~3.3 KB, several 1024-byte blocks

        return ContentPayloads.Encode(contentFormat, "text", text);
    }
}

/// <summary><c>/coap/echo</c> -- echoes the request payload back verbatim in the response.</summary>
public sealed class EchoResource : ResourceBase
{
    public override string Path => "/coap/echo";
    public override string Title => "Echo Demo";
    public override string? ResourceType => "coap.demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Json, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get, CoapMethods.Post];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
        => Task.FromResult(Ok(ex.PayloadBytes, ex.RequestContentFormat ?? contentFormat));
}

/// <summary>Shared helpers for encoding the same logical value into whichever format was negotiated.</summary>
public static class ContentPayloads
{
    public static byte[] Encode(int contentFormat, string fieldName, string textValue) => contentFormat switch
    {
        Protocol.ContentFormats.Json => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [fieldName] = textValue }, CoapJsonContext.Default.DictionaryStringString),
        Protocol.ContentFormats.Cbor => Cbor.CborHelpers.EncodeSingleFieldMap(fieldName, textValue),
        _ => Encoding.UTF8.GetBytes(textValue),
    };

    public static byte[] EncodeNumber(int contentFormat, string fieldName, long value) => contentFormat switch
    {
        Protocol.ContentFormats.Json => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, long> { [fieldName] = value }, CoapJsonContext.Default.DictionaryStringInt64),
        Protocol.ContentFormats.Cbor => Cbor.CborHelpers.EncodeMap([(fieldName, value)]),
        _ => Encoding.UTF8.GetBytes(value.ToString()),
    };
}
