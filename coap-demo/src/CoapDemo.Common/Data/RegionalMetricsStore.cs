using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace CoapDemo.Common.Data;

/// <summary>
/// Per-region DynamoDB table used as the write-side of the "System Resource Updates" pipeline
/// (design.md): the request handler writes here on every request; a regional stream handler
/// (<c>RegionalAggregator</c>) reads the stream and folds the change into the Global table.
///
/// Single-table layout:
///  - PK = "METRIC#REQUEST", SK = "&lt;epoch-ticks&gt;#&lt;guid&gt;"   -- one throwaway item per
///    request (TTL'd quickly); each INSERT is a discrete +1 event the aggregator ADDs to the
///    global counter, which keeps the aggregation commutative regardless of write order.
///  - PK = "TIME#/system/time", SK = "STATE"                          -- last-request-time,
///    overwritten on every request; the aggregator forwards the latest value it observes.
///  - PK = "RATE#{path}", SK = "STATE"                                -- last broadcast epoch,
///    used by GlobalNotifier to rate-limit notification broadcasts to at most once per minute.
///  - PK = "UPLOAD#{address}:{port}#{token}", SK = "{path}"           -- Block1 upload scratch
///    state (bytes received so far); TTL'd shortly so abandoned transfers self-clean.
/// </summary>
public sealed class RegionalMetricsStore(IAmazonDynamoDB ddb, string tableName)
{
    private readonly IAmazonDynamoDB _ddb = ddb;
    private readonly string _tableName = tableName;

    /// <summary>Records one "+1 request" event that the RegionalAggregator will fold into the global counter.</summary>
    public Task RecordRequestAsync(CancellationToken ct = default)
        => _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = "METRIC#REQUEST" },
                ["SK"] = new() { S = $"{DateTimeOffset.UtcNow.Ticks:D20}#{Guid.NewGuid():N}" },
                ["Count"] = new() { N = "1" },
                ["Expires"] = new() { N = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300).ToString() },
            }
        }, ct);

    /// <summary>Overwrites the regional "last request time" item; the aggregator forwards this value.</summary>
    public Task RecordRequestTimeAsync(string isoTimestamp, CancellationToken ct = default)
        => _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = "TIME#/system/time" },
                ["SK"] = new() { S = "STATE" },
                ["LastRequestTime"] = new() { S = isoTimestamp },
            }
        }, ct);

    /// <summary>Returns true (and records the attempt) if a broadcast for <paramref name="path"/> is allowed right now, rate-limited to once per <paramref name="minIntervalSeconds"/>.</summary>
    public async Task<bool> TryAcquireBroadcastAsync(string path, int minIntervalSeconds, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            await _ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new() { S = $"RATE#{path}" },
                    ["SK"] = new() { S = "STATE" },
                },
                UpdateExpression = "SET LastBroadcast = :now",
                ConditionExpression = "attribute_not_exists(LastBroadcast) OR LastBroadcast < :cutoff",
                ExpressionAttributeValues = new()
                {
                    { ":now", new AttributeValue { N = now.ToString() } },
                    { ":cutoff", new AttributeValue { N = (now - minIntervalSeconds).ToString() } },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    // ── Block1 upload scratch state (design.md "Block-Wise Transfer") ─────────────────────────

    public async Task<long> GetUploadBytesReceivedAsync(string address, int port, string token, string path, CancellationToken ct = default)
    {
        var response = await _ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = $"UPLOAD#{address}:{port}#{token}" },
                ["SK"] = new() { S = path },
            }
        }, ct);
        return response.IsItemSet && response.Item.TryGetValue("BytesReceived", out var v) ? long.Parse(v.N) : 0;
    }

    public Task PutUploadBytesReceivedAsync(string address, int port, string token, string path, long bytesReceived, CancellationToken ct = default)
        => _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = $"UPLOAD#{address}:{port}#{token}" },
                ["SK"] = new() { S = path },
                ["BytesReceived"] = new() { N = bytesReceived.ToString() },
                ["Expires"] = new() { N = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60).ToString() },
            }
        }, ct);

    public Task DeleteUploadStateAsync(string address, int port, string token, string path, CancellationToken ct = default)
        => _ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = $"UPLOAD#{address}:{port}#{token}" },
                ["SK"] = new() { S = path },
            }
        }, ct);
}
