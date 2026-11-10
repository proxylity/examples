using Amazon.DynamoDBv2;
using DnsBlocklistSync;
using System.Diagnostics;
using System.Text.Json;

var profile = Environment.GetEnvironmentVariable("BLOCKLIST_PROFILE") ?? "Balanced";
var mode = Environment.GetEnvironmentVariable("WORKER_MODE") ?? "sync";
var timer = Stopwatch.StartNew();
SyncWorker? worker = null;
var success = false;
try
{
    var sources = Catalog.For(profile);
    var table = Environment.GetEnvironmentVariable("TABLE_NAME");
    if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("TABLE_NAME is required.");
    var cache = Environment.GetEnvironmentVariable("CACHE_DIRECTORY") ?? "/tmp/dns-sync-cache";
    if (string.IsNullOrWhiteSpace(cache)) throw new ArgumentException("CACHE_DIRECTORY cannot be empty.");
    if (mode == "validate")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "validated-not-synced", profile, table,
            cache, schema = 2, sources, checkpointMutated = false }));
        return 0;
    }
    if (mode != "sync") throw new ArgumentException("WORKER_MODE must be sync or validate.");
    var rollbackSource = Environment.GetEnvironmentVariable("ROLLBACK_SOURCE");
    var rollbackCommit = Environment.GetEnvironmentVariable("ROLLBACK_COMMIT");
    if (!string.IsNullOrEmpty(rollbackSource) || !string.IsNullOrEmpty(rollbackCommit))
    {
        if (string.IsNullOrEmpty(rollbackSource) || string.IsNullOrEmpty(rollbackCommit) ||
            !sources.Any(s => s.Id == rollbackSource)) throw new ArgumentException("Rollback requires an enabled source and full commit ID.");
        Console.WriteLine($"Requested inverse-delta rollback for {rollbackSource} to {rollbackCommit}.");
    }
    using var client = new AmazonDynamoDBClient();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    worker = new(new DynamoStore(client, table), new GitSource(), cache);
    await worker.Run(profile, sources, cancellation.Token, rollbackSource, rollbackCommit);
    success = true;
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        _aws = new { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CloudWatchMetrics = new[] { new { Namespace = "DnsFilter/Sync", Dimensions = new[] { new[] { "Profile" } },
                Metrics = new[] { new { Name = "Added", Unit = "Count" }, new { Name = "Removed", Unit = "Count" },
                    new { Name = "ProtectedConflicts", Unit = "Count" }, new { Name = "Success", Unit = "Count" },
                    new { Name = "Duration", Unit = "Seconds" } } } } },
        Profile = profile, Added = worker?.Added ?? 0, Removed = worker?.Removed ?? 0,
        ProtectedConflicts = worker?.ProtectedConflicts ?? 0, Success = success ? 1 : 0,
        Duration = timer.Elapsed.TotalSeconds, Mode = mode
    }));
}
