using System.Linq.Expressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace DnsFilterLambda;


public class DomainStateStore : IDomainStateStore
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DomainStateStore(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    public async Task<DomainState?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(domain))
            throw new ArgumentException("Domain cannot be null or empty.", nameof(domain));

        // Fetch the item from DynamoDB
        var response = await _dynamoDb.GetItemAsync(_tableName, new()
        {
            { "PK", new(domain) },
            { "SK", new("STATE") }
        }, cancellationToken);

        if (response.Item == null || response.Item.Count == 0)
            return null;

        return new()
        {
            Domain = domain,
            IsSuspicious = response.Item.TryGetValue("IsSuspicious", out var suspicious) && suspicious.BOOL == true,
            TotalQueries = int.Parse(response.Item["TotalQueries"].N),
            NxDomainCount = int.Parse(response.Item["NxDomainCount"].N),
            Version = long.Parse(response.Item["Version"].N),
            UniqueSubdomains = DeserializeHll(response.Item["HllState"].B.ToArray()),
            Expires = response.Item.TryGetValue("TTL", out AttributeValue? value) ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(value.N)).UtcDateTime
                : DateTimeOffset.UtcNow.AddDays(7) // Default to 7 days if no expiry set
        };
    }

    public async Task<DomainState?[]> BatchGetAsync(ICollection<string> domains, bool fillMissing = true, CancellationToken cancellationToken = default)
    {
        if (domains == null || domains.Count == 0)
            throw new ArgumentException("Domains cannot be null or empty.", nameof(domains));

        if (domains.Any(d => string.IsNullOrEmpty(d)))
            throw new ArgumentException("All domains must be non-empty strings.", nameof(domains));

        var keys = domains.Select(domain => new Dictionary<string, AttributeValue>
        {
            { "PK", new() { S = domain } },
            { "SK", new() { S = "STATE" } }
        }).ToList();

        var items = new List<Dictionary<string, AttributeValue>>();
        foreach (var batch in keys.Chunk(100))
        {
            var pending = new Dictionary<string, KeysAndAttributes>
            {
                [_tableName] = new() { Keys = [.. batch], ConsistentRead = true }
            };
            for (var attempt = 0; pending.Count > 0; attempt++)
            {
                var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = pending }, cancellationToken);
                items.AddRange(response.Responses?.GetValueOrDefault(_tableName, []) ?? []);
                pending = response.UnprocessedKeys?.Where(kv => kv.Value.Keys?.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
                if (pending.Count == 0) break;
                if (attempt >= 4)
                {
                    await Console.Error.WriteLineAsync("Domain-state reads remained unprocessed after five attempts.");
                    throw new InvalidOperationException("Incomplete domain-state reads; refusing to treat unread domains as new.");
                }
                foreach (var attributes in pending.Values) attributes.ConsistentRead = true;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), cancellationToken);
            }
        }

        var states = items.ToDictionary(item => item["PK"].S, item => new DomainState
        {
            Domain = item["PK"].S,
            IsSuspicious = item.TryGetValue("IsSuspicious", out var suspicious) && suspicious.BOOL == true,
            TotalQueries = int.Parse(item["TotalQueries"].N),
            NxDomainCount = int.Parse(item["NxDomainCount"].N),
            Version = long.Parse(item["Version"].N),
            UniqueSubdomains = DeserializeHll(item["HllState"].B.ToArray()),
            Expires = item.TryGetValue("TTL", out AttributeValue? value) ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(value.N)).UtcDateTime
                    : DateTimeOffset.UtcNow.AddDays(7) // Default to 7 days if no expiry set
        });
        return [.. domains.Select(domain => states.TryGetValue(domain, out var state) ? state :
            fillMissing ? new DomainState { Domain = domain } : null)];
    }

    public async Task BatchUpdateAsync(ICollection<DomainState> updatedStates, CancellationToken cancellationToken = default)
    {
        // Each transaction atomically updates at most 100 version-guarded domain states.
        foreach (var batch in updatedStates.Chunk(100))
        {
            var request = new TransactWriteItemsRequest
            {
                TransactItems = [.. batch.Select(state => new TransactWriteItem
                {
                    Update = new()
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            { "PK", new() { S = state.Domain } },
                            { "SK", new() { S = "STATE" } }
                        },
                        UpdateExpression = "SET TotalQueries = :total, NxDomainCount = :nx, HllState = :hll, IsSuspicious = :suspicious, #ttl = :ttl, #version = if_not_exists(#version,:zero) + :one",
                        ConditionExpression = "attribute_not_exists(PK) OR #version = :expectedVersion",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            { "#ttl", "TTL" },
                            { "#version", "Version" }
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            { ":total", new() { N = state.TotalQueries.ToString() } },
                            { ":nx", new() { N = state.NxDomainCount.ToString() } },
                            { ":hll", new() { B = new MemoryStream(state.UniqueSubdomains.Serialize()) } },
                            { ":suspicious", new() { BOOL = state.IsSuspicious } },
                            { ":zero", new() { N = "0" } },
                            { ":one", new() { N = "1" } },
                            { ":expectedVersion", new() { N = state.Version.ToString() } },
                            { ":ttl", new() { N = state.Expires.ToUnixTimeSeconds().ToString() } }
                        }
                    }
                })]
            };

            await Console.Out.WriteLineAsync($"Batch updating {batch.Length} domain states.");

            try
            {
                var response = await _dynamoDb.TransactWriteItemsAsync(request, cancellationToken);
                await Console.Out.WriteLineAsync($"Batch update response: {response.HttpStatusCode}");
            }
            catch (TransactionCanceledException ex)
            {
                // Handle transaction cancellation, which may occur due to conditional check failures
                await Console.Error.WriteLineAsync($"Transaction canceled: {ex.Message}");
                foreach (var item in ex.CancellationReasons)
                {
                    await Console.Error.WriteLineAsync($"Reason: {item.Message}");
                }
                throw; // rethrow to let the caller handle it
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Batch update failed: {ex.Message}");
                throw; // rethrow to let the caller handle it
            }
        }
    }

    public async Task UpdateAsync(DomainState updatedState, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;
        int attempt = 0;
        var delay = TimeSpan.FromMilliseconds(100);

        while (attempt < maxRetries)
        {
            await Console.Out.WriteLineAsync($"Updating a domain state, version {updatedState.Version}");
            try
            {
                var request = new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new() {
                        { "PK", new(updatedState.Domain) },
                        { "SK", new("STATE") }
                    },
                    UpdateExpression = "SET TotalQueries = :total, NxDomainCount = :nx, HllState = :hll, IsSuspicious = :suspicious, #ttl = :ttl, #version = if_not_exists(#version,:zero) + :one",
                    ConditionExpression = "attribute_not_exists(PK) OR Version = :expectedVersion",
                    ExpressionAttributeNames = new()
                    {
                        { "#ttl", "TTL" },
                        { "#version", "Version" }
                    },
                    ExpressionAttributeValues = new()
                    {
                        { ":total", new() { N = updatedState.TotalQueries.ToString() } },
                        { ":nx", new() { N = updatedState.NxDomainCount.ToString() } },
                        { ":hll", new() { B = new MemoryStream(updatedState.UniqueSubdomains.Serialize()) } },
                        { ":suspicious", new() { BOOL = updatedState.IsSuspicious } },
                        { ":zero", new() { N = "0" } },
                        { ":one", new() { N = "1" } },
                        { ":expectedVersion", new() { N = updatedState.Version.ToString() } },
                        { ":ttl", new() { N = updatedState.Expires.ToUnixTimeSeconds().ToString() } }
                    }
                };

                await _dynamoDb.UpdateItemAsync(request, cancellationToken);
                return; // success
            }
            catch (ConditionalCheckFailedException)
            {
                // Concurrent modification detected, refresh state and retry
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay *= 2; // exponential backoff

                var latestState = await GetAsync(updatedState.Domain, cancellationToken) ?? throw new Exception("State unexpectedly missing.");

                // Merge our local HLL into the latest version
                latestState.UniqueSubdomains.Merge(updatedState.UniqueSubdomains);
                latestState.IsSuspicious |= updatedState.IsSuspicious;
                latestState.TotalQueries += updatedState.TotalQueries - latestState.TotalQueries;
                latestState.NxDomainCount += updatedState.NxDomainCount - latestState.NxDomainCount;

                updatedState = latestState; // retry with the merged state
            }
        }

        throw new Exception("Failed to update after maximum retries.");
    }

    private static HyperLogLogEstimator DeserializeHll(byte[] data)
    {
        var hll = new HyperLogLogEstimator();
        hll.Deserialize(data);
        return hll;
    }
}
