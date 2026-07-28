using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace CoapDemo.Common.Data;

/// <summary>Current value of a <c>/system/*</c> resource, read from the Global DynamoDB table.</summary>
public sealed record SystemState(string Path, long RequestCount, string? LastRequestTime, string? Status, string? Version, string LastWriteRegion);

/// <summary>
/// Live <c>/system/*</c> resource values, backed by the multi-region Global DynamoDB table.
/// Single-table layout: PK = "SYS#{path}", SK = "STATE". Populated by <see cref="Data.RegionalMetricsStore"/>
/// via the regional-aggregation write pipeline described in design.md "System Resource Updates",
/// except for health/version which are seeded once per region at cold start (see design.md's
/// "written at deployment time").
/// </summary>
public sealed class SystemStateStore(IAmazonDynamoDB ddb, string tableName)
{
    private readonly IAmazonDynamoDB _ddb = ddb;
    private readonly string _tableName = tableName;

    public const string HealthPath = "/system/health";
    public const string VersionPath = "/system/version";
    public const string MetricsPath = "/system/metrics";
    public const string TimePath = "/system/time";

    public static string BuildPk(string path) => $"SYS#{path}";

    public async Task<SystemState?> GetAsync(string path, CancellationToken ct = default)
    {
        var response = await _ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = BuildPk(path) },
                ["SK"] = new() { S = "STATE" },
            }
        }, ct);

        if (!response.IsItemSet) return null;
        var item = response.Item;
        return new SystemState(
            Path: path,
            RequestCount: item.TryGetValue("RequestCount", out var rc) ? long.Parse(rc.N) : 0,
            LastRequestTime: item.TryGetValue("LastRequestTime", out var lrt) ? lrt.S : null,
            Status: item.TryGetValue("Status", out var s) ? s.S : null,
            Version: item.TryGetValue("Version", out var v) ? v.S : null,
            LastWriteRegion: item.TryGetValue("LastWriteRegion", out var r) ? r.S : "");
    }

    /// <summary>Idempotently seeds a static (health/version) item on cold start if it does not already exist.</summary>
    public Task SeedIfMissingAsync(string path, string attributeName, string value, string region, CancellationToken ct = default)
        => _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = BuildPk(path) },
                ["SK"] = new() { S = "STATE" },
                ["Path"] = new() { S = path },
                [attributeName] = new() { S = value },
                ["LastWriteRegion"] = new() { S = region },
                ["UpdatedAt"] = new() { S = DateTimeOffset.UtcNow.ToString("O") },
            },
            ConditionExpression = "attribute_not_exists(PK)",
        }, ct).ContinueWith(t =>
        {
            // A ConditionalCheckFailedException just means another warm invocation (or a
            // previous deployment) already seeded this value -- nothing to do.
            if (t.IsFaulted && t.Exception?.InnerException is not ConditionalCheckFailedException)
                throw t.Exception!;
        }, ct);
}
