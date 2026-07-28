using System.Text;
using CoapDemo.Common.Protocol;

namespace CoapDemo.Common.Resources;

/// <summary>
/// Static, hardcoded informational content mirroring the Proxylity website (design.md
/// "/info/*"). One class serves every <c>/info/*</c> resource; only the path/title/text differ.
/// </summary>
public sealed class StaticTextResource(string path, string title, string text) : ResourceBase
{
    public override string Path => path;
    public override string Title => title;
    public override string? ResourceType => "info";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain];
    public override IReadOnlyList<int> AllowedMethods => [CoapMethods.Get];

    protected override Task<CoapResult> ExecuteAsync(CoapExchange ex, int contentFormat, CancellationToken ct)
        => Task.FromResult(Ok(Encoding.UTF8.GetBytes(text), contentFormat));
}
