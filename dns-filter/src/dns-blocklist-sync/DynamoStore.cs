using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DnsBlocklistSync;

public sealed class DynamoStore(IAmazonDynamoDB client, string table) : IStore
{
    private const string Owner = "blocklist-sync";
    private readonly string leaseOwner = Guid.NewGuid().ToString("N");
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static AttributeValue S(string text) => new() { S = text };
    private static AttributeValue N(long number) => new() { N = number.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> Key(string key) => new() { ["PK"] = S(key), ["SK"] = S(key) };
    private async Task<T> Retry<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (attempt < 7 && Transient(ex))
            {
                Console.Error.WriteLine($"Retrying DynamoDB operation after {ex.GetType().Name} (attempt {attempt + 1}/7).");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 100 * (1 << attempt)) +
                    Random.Shared.Next(100)), token);
            }
        }
    }
    private static bool Transient(Exception ex) => ex is AmazonDynamoDBException aws &&
        ((int)aws.StatusCode >= 500 || aws is ProvisionedThroughputExceededException ||
        aws.ErrorCode is "ThrottlingException" or
        "ProvisionedThroughputExceededException" or "RequestLimitExceeded");

    public async Task Acquire(CancellationToken token) =>
        await Retry(() => client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table, Key = Key("SYNC#LEASE"),
            UpdateExpression = "SET #owner = :owner, expires = :expires",
            ConditionExpression = "attribute_not_exists(PK) OR expires < :now",
            ExpressionAttributeNames = new() { ["#owner"] = "owner" },
            ExpressionAttributeValues = new() { [":owner"] = S(leaseOwner), [":expires"] = N(Now + 120), [":now"] = N(Now) }
        }, token), token);
    public async Task Renew(CancellationToken token) =>
        await Retry(() => client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table, Key = Key("SYNC#LEASE"), UpdateExpression = "SET expires = :expires",
            ConditionExpression = "#owner = :owner AND expires >= :now",
            ExpressionAttributeNames = new() { ["#owner"] = "owner" },
            ExpressionAttributeValues = new() { [":owner"] = S(leaseOwner), [":expires"] = N(Now + 120), [":now"] = N(Now) }
        }, token), token);
    public async Task Release(CancellationToken token)
    {
        try
        {
            await Retry(() => client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table, Key = Key("SYNC#LEASE"), ConditionExpression = "#owner = :owner",
                ExpressionAttributeNames = new() { ["#owner"] = "owner" },
                ExpressionAttributeValues = new() { [":owner"] = S(leaseOwner) }
            }, token), token);
        }
        catch (ConditionalCheckFailedException) { }
    }
    private async Task<Dictionary<string, AttributeValue>> Read(string key, CancellationToken token) =>
        (await Retry(() => client.GetItemAsync(new GetItemRequest
        { TableName = table, Key = Key(key), ConsistentRead = true }, token), token)).Item ?? [];

    public async Task<DurableState?> ReadState(CancellationToken token)
    {
        var item = await Read("SYNC#STATE", token);
        return item.TryGetValue("stateJson", out var json) ? JsonSerializer.Deserialize<DurableState>(json.S!) : null;
    }
    public async Task SaveState(DurableState state, CancellationToken token)
    {
        var item = Key("SYNC#STATE");
        item["status"] = S(state.Status);
        item["stateJson"] = S(JsonSerializer.Serialize(state));
        item["lastSuccess"] = N(state.LastSuccess);
        item["profile"] = S(state.Checkpoint?.Profile ?? "");
        item["sourceCommits"] = S(JsonSerializer.Serialize(state.Checkpoint?.Lists.ToDictionary(x => x.Key, x => x.Value.Commit)
            ?? new Dictionary<string, string>()));
        if (state.Error != null) item["error"] = S(state.Error);
        await Retry(() => client.PutItemAsync(new PutItemRequest { TableName = table, Item = item }, token), token);
    }
    public async Task<WriteResult> Apply(Change change, CancellationToken token)
    {
        try
        {
            var values = new Dictionary<string, AttributeValue>
            { [":sync"] = S(Owner), [":list"] = new() { SS = [change.Source] } };
            var names = new Dictionary<string, string> { ["#managed"] = "managedBy" };
            var update = new UpdateItemRequest
            {
                TableName = table, Key = Key(change.Key), ExpressionAttributeValues = values,
                ExpressionAttributeNames = names,
                ConditionExpression = change.Add ? "attribute_not_exists(PK) OR #managed = :sync" : "#managed = :sync",
                UpdateExpression = change.Add
                    ? "SET #managed = :sync, blocked = :blocked, ManagedPK = :shard, ManagedSK = :key ADD sourceLists :list"
                    : "DELETE sourceLists :list"
            };
            if (change.Add)
            {
                values[":blocked"] = new() { BOOL = true };
                values[":shard"] = S(Entries.Shard(change.Key));
                values[":key"] = S(change.Key);
            }
            await Retry(() => client.UpdateItemAsync(update, token), token);
            if (!change.Add) await Cleanup(change.Key, token);
            return WriteResult.Applied;
        }
        catch (ConditionalCheckFailedException)
        {
            var item = await Read(change.Key, token);
            return item.Count == 0 ? WriteResult.Absent : WriteResult.Protected;
        }
    }
    public async Task Cleanup(string key, CancellationToken token)
    {
        try
        {
            await Retry(() => client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table, Key = Key(key),
                ConditionExpression = "managedBy = :sync AND attribute_not_exists(sourceLists)",
                ExpressionAttributeValues = new() { [":sync"] = S(Owner) }
            }, token), token);
        }
        catch (ConditionalCheckFailedException) { }
    }
    public async IAsyncEnumerable<ManagedRecord> ReadManaged([EnumeratorCancellation] CancellationToken token)
    {
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var page = await Retry(() => client.ScanAsync(new ScanRequest
            {
                TableName = table, ConsistentRead = true, ExclusiveStartKey = start,
                FilterExpression = "managedBy = :sync",
                ExpressionAttributeValues = new() { [":sync"] = S(Owner) },
                ProjectionExpression = "PK, SK, sourceLists"
            }, token), token);
            foreach (var item in page.Items ?? [])
            {
                var key = item["PK"].S!;
                if (item["SK"].S != key || key.StartsWith("SYNC#", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid managed policy key.");
                yield return new(key, item.TryGetValue("sourceLists", out var lists) ? (lists.SS ?? []).ToHashSet() : []);
            }
            start = page.LastEvaluatedKey;
        } while (start?.Count > 0);
    }
}
