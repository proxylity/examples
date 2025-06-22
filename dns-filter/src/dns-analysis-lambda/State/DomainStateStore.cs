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
            TotalQueries = int.Parse(response.Item["TotalQueries"].N),
            NxDomainCount = int.Parse(response.Item["NxDomainCount"].N),
            Version = long.Parse(response.Item["Version"].N),
            UniqueSubdomains = DeserializeHll(response.Item["HllState"].B.ToArray()),
            Expires = response.Item.TryGetValue("TTL", out AttributeValue? value) ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(value.N)).UtcDateTime 
                : DateTimeOffset.UtcNow.AddDays(1) // Default to 1 day if no expiry set
        };
    }

    public async Task<DomainState?[]> BatchGetAsync(ICollection<string> domains, CancellationToken cancellationToken = default)
    {
        if (domains == null || domains.Count == 0)
            throw new ArgumentException("Domains cannot be null or empty.", nameof(domains));

        var keys = domains.Select(domain => new Dictionary<string, AttributeValue>
        {
            { "PK", new AttributeValue { S = domain } },
            { "SK", new AttributeValue { S = "STATE" } }
        }).ToList();

        var request = new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [_tableName] = new() { Keys = keys }
            }
        };

        var response = await _dynamoDb.BatchGetItemAsync(request, cancellationToken);
        var items = response.Responses.GetValueOrDefault(_tableName, []);

        return [.. items.Select(item => new DomainState
        {
            Domain = item["PK"].S,
            TotalQueries = int.Parse(item["TotalQueries"].N),
            NxDomainCount = int.Parse(item["NxDomainCount"].N),
            Version = long.Parse(item["Version"].N),
            UniqueSubdomains = DeserializeHll(item["HllState"].B.ToArray()),
            Expires = item.TryGetValue("TTL", out AttributeValue? value) ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(value.N)).UtcDateTime 
                : DateTimeOffset.UtcNow.AddDays(1) // Default to 1 day if no expiry set
        })];
    }

    public async Task UpdateAsync(DomainState updatedState, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;
        int attempt = 0;
        var delay = TimeSpan.FromMilliseconds(100);

        while (attempt < maxRetries)
        {
            try
            {
                var request = new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new() {
                        { "PK", new(updatedState.Domain) },
                        { "SK", new("STATE") }
                    },
                    UpdateExpression = "SET TotalQueries = :total, NxDomainCount = :nx, HllState = :hll, TTL = :ttl, Version = Version + :one",
                    ConditionExpression = "Version = :expectedVersion",
                    ExpressionAttributeValues = new()
                    {
                        { ":total", new() { N = updatedState.TotalQueries.ToString() } },
                        { ":nx", new() { N = updatedState.NxDomainCount.ToString() } },
                        { ":hll", new() { B = new MemoryStream(updatedState.UniqueSubdomains.Serialize()) } },
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
                await Task.Delay(delay);
                delay *= 2; // exponential backoff

                var latestState = await GetAsync(updatedState.Domain, cancellationToken) ?? throw new Exception("State unexpectedly missing.");

                // Merge our local HLL into the latest version
                latestState.UniqueSubdomains.Merge(updatedState.UniqueSubdomains);
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
