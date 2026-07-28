using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// <c>/demo/upload</c> -- accepts an RFC 7959 Block1 block-wise upload. Each block is
/// validated and counted as it arrives (byte count tracked in the regional DynamoDB table,
/// keyed by client endpoint + token); the raw bytes themselves are never persisted, per
/// design.md "Block-Wise Transfer" / "Content Security".
/// </summary>
public sealed class UploadResource(RegionalMetricsStore metrics) : ResourceBase
{
    public override string Path => "/demo/upload";
    public override string Title => "Block-Wise Upload Demo (Block1)";
    public override string? ResourceType => "demo";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Post];

    protected override async Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
    {
        var token = ex.Request.Token ?? "";
        var (blockNumber, more, szx, _) = ex.HasOption(CoapOptionNumbers.Block1)
            ? CoapCodec.DecodeBlockOption(ex.GetOption(CoapOptionNumbers.Block1)!.Value)
            : (0, false, 6, 0);

        var chunkLength = ex.PayloadBytes.Length;
        var previous = blockNumber == 0 ? 0 : await metrics.GetUploadBytesReceivedAsync(ex.ClientAddress, ex.ClientPort, token, Path, ct);
        var total = previous + chunkLength;

        if (more)
        {
            await metrics.PutUploadBytesReceivedAsync(ex.ClientAddress, ex.ClientPort, token, Path, total, ct);
            var options = new List<CoapOption> { new(CoapOptionNumbers.Block1, CoapCodec.EncodeBlockOption(blockNumber, true, szx)) };
            return new CoapResult { Code = CoapCodes.Continue, Options = options };
        }

        var declaredSize = ex.GetOptionUInt(CoapOptionNumbers.Size1);
        await metrics.DeleteUploadStateAsync(ex.ClientAddress, ex.ClientPort, token, Path, ct);

        if (declaredSize is not null && declaredSize.Value != total)
            return Problem(CoapCodes.BadRequest, $"Declared Size1={declaredSize} does not match {total} bytes received.");

        var payload = ContentPayloads.EncodeNumber(contentFormat, "bytesReceived", total);
        var finalOptions = new List<CoapOption>();
        if (ex.HasOption(CoapOptionNumbers.Block1))
            finalOptions.Add(new CoapOption(CoapOptionNumbers.Block1, CoapCodec.EncodeBlockOption(blockNumber, false, szx)));

        return new CoapResult
        {
            Code = CoapCodes.Changed,
            Options = [.. finalOptions, new CoapOption(CoapOptionNumbers.ContentFormat, CoapCodec.UIntToBase64(contentFormat))],
            Payload = payload,
        };
    }
}
