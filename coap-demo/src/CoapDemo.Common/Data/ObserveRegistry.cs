using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace CoapDemo.Common.Data;

/// <summary>An active Observe subscription (see design.md "Observe Registry").</summary>
public sealed record ObserveSubscription(
    string Path,
    string Address,
    int Port,
    string Token,
    int ContentFormat,
    long Expires,
    string LastWriteRegion);

/// <summary>
/// Observe subscription registry, backed by the multi-region Global DynamoDB table.
/// Single-table layout: PK = "OBS#{path}", SK = "{address}:{port}#{token}".
/// </summary>
public sealed class ObserveRegistry(IAmazonDynamoDB ddb, string tableName)
{
    private readonly IAmazonDynamoDB _ddb = ddb;
    private readonly string _tableName = tableName;

    public static string BuildPk(string path) => $"OBS#{path}";
    public static string BuildSk(string address, int port, string token) => $"{address}:{port}#{token}";

    /// <summary>Registers (or refreshes) a subscription. TTL is <paramref name="ttlSeconds"/> from now.</summary>
    public Task RegisterAsync(string path, string address, int port, string token, int contentFormat, string region, int ttlSeconds, CancellationToken ct = default)
    {
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
        return _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = BuildPk(path) },
                ["SK"] = new() { S = BuildSk(address, port, token) },
                ["Path"] = new() { S = path },
                ["Address"] = new() { S = address },
                ["Port"] = new() { N = port.ToString() },
                ["Token"] = new() { S = token },
                ["ContentFormat"] = new() { N = contentFormat.ToString() },
                ["Expires"] = new() { N = expires.ToString() },
                ["LastWriteRegion"] = new() { S = region },
                ["Sequence"] = new() { N = "0" },
            }
        }, ct);
    }

    public Task DeregisterAsync(string path, string address, int port, string token, CancellationToken ct = default)
        => _ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = BuildPk(path) },
                ["SK"] = new() { S = BuildSk(address, port, token) },
            }
        }, ct);

    /// <summary>Queries every non-expired subscriber for <paramref name="path"/>, across all regions.</summary>
    public async Task<List<ObserveSubscription>> ListAsync(string path, CancellationToken ct = default)
    {
        var results = new List<ObserveSubscription>();
        Dictionary<string, AttributeValue>? lastKey = null;
        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        do
        {
            var response = await _ddb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk",
                ExpressionAttributeValues = new() { { ":pk", new AttributeValue { S = BuildPk(path) } } },
                ExclusiveStartKey = lastKey is { Count: > 0 } ? lastKey : null,
            }, ct);

            foreach (var item in response.Items)
            {
                if (!item.TryGetValue("Expires", out var expiresAttr) || !long.TryParse(expiresAttr.N, out var expires) || expires <= nowEpoch)
                    continue;

                results.Add(new ObserveSubscription(
                    Path: item["Path"].S,
                    Address: item["Address"].S,
                    Port: int.Parse(item["Port"].N),
                    Token: item["Token"].S,
                    ContentFormat: int.Parse(item["ContentFormat"].N),
                    Expires: expires,
                    LastWriteRegion: item.TryGetValue("LastWriteRegion", out var r) ? r.S : ""));
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey is { Count: > 0 });

        return results;
    }

    /// <summary>Counts every non-expired subscriber for <paramref name="path"/>, across all regions.</summary>
    public async Task<int> CountAsync(string path, CancellationToken ct = default)
        => (await ListAsync(path, ct)).Count;
}
