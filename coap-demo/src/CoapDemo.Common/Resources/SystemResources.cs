using CoapDemo.Common.Data;

namespace CoapDemo.Common.Resources;

/// <summary>
/// Base for the four <c>/system/*</c> resources. All four are Observe-capable and backed by
/// the multi-region Global DynamoDB table rather than in-memory or computed-on-request state
/// (design.md "Observe" / "System Resource Updates"). Health/version are written once at
/// deployment (cold start); metrics/time are kept current by the request-handling pipeline
/// via the regional-table -> RegionalAggregator -> global-table write path.
/// </summary>
public abstract class SystemResourceBase(SystemStateStore store, ObserveRegistry registry, int observeTtlSeconds)
    : ObservableResourceBase(registry, observeTtlSeconds)
{
    protected readonly SystemStateStore Store = store;

    public override string? ResourceType => "system";
    public override IReadOnlyList<int> ContentFormats => [Protocol.ContentFormats.TextPlain, Protocol.ContentFormats.Cbor];
    public override IReadOnlyList<int> AllowedMethods => [Protocol.CoapMethods.Get];
}

/// <summary><c>/system/health</c> -- current service health status, written at deployment time.</summary>
public sealed class HealthResource(SystemStateStore store, ObserveRegistry registry, int observeTtlSeconds)
    : SystemResourceBase(store, registry, observeTtlSeconds)
{
    public override string Path => SystemStateStore.HealthPath;
    public override string Title => "Service Health";

    protected override async Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct)
    {
        var state = await Store.GetAsync(Path, ct);
        return ContentPayloads.Encode(contentFormat, "status", state?.Status ?? "unknown");
    }
}

/// <summary><c>/system/version</c> -- deployed Lambda version, written at deployment time.</summary>
public sealed class VersionResource(SystemStateStore store, ObserveRegistry registry, int observeTtlSeconds)
    : SystemResourceBase(store, registry, observeTtlSeconds)
{
    public override string Path => SystemStateStore.VersionPath;
    public override string Title => "Deployed Version";

    protected override async Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct)
    {
        var state = await Store.GetAsync(Path, ct);
        return ContentPayloads.Encode(contentFormat, "version", state?.Version ?? "unknown");
    }
}

/// <summary><c>/system/metrics</c> -- aggregate request count across every request handled, in every region.</summary>
public sealed class MetricsResource(SystemStateStore store, ObserveRegistry registry, int observeTtlSeconds)
    : SystemResourceBase(store, registry, observeTtlSeconds)
{
    public override string Path => SystemStateStore.MetricsPath;
    public override string Title => "Aggregate Request Metrics";

    protected override async Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct)
    {
        var state = await Store.GetAsync(Path, ct);
        return ContentPayloads.EncodeNumber(contentFormat, "requests", state?.RequestCount ?? 0);
    }
}

/// <summary><c>/system/time</c> -- server time at the last invocation handled by any region.</summary>
public sealed class TimeResource(SystemStateStore store, ObserveRegistry registry, int observeTtlSeconds)
    : SystemResourceBase(store, registry, observeTtlSeconds)
{
    public override string Path => SystemStateStore.TimePath;
    public override string Title => "Last Invocation Time";

    protected override async Task<byte[]> RenderCurrentValueAsync(int contentFormat, CancellationToken ct)
    {
        var state = await Store.GetAsync(Path, ct);
        return ContentPayloads.Encode(contentFormat, "time", state?.LastRequestTime ?? DateTimeOffset.UtcNow.ToString("O"));
    }
}
