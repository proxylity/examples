using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace RegionalAggregator;

/// <summary>
/// Regional stream handler for the write side of design.md's "System Resource Updates"
/// pipeline: reads the regional DynamoDB table's stream and folds each change into the
/// Global table using an atomic, commutative <c>ADD</c> (request counts) or a plain
/// last-write forward (last invocation time) -- never a full-item <c>PUT</c>, which would
/// otherwise let concurrent regional writers silently overwrite each other's updates once
/// DynamoDB Global Tables replicates the item.
/// </summary>
public sealed class Function(IAmazonDynamoDB ddb)
{
    private readonly IAmazonDynamoDB _ddb = ddb;
    private static readonly string GlobalTableName = Env("GLOBAL_TABLE_NAME");
    private static readonly string Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown";

    public Function() : this(new AmazonDynamoDBClient())
    {
    }

    public async Task FunctionHandler(DynamoDBEvent evnt, ILambdaContext context)
    {
        var tasks = evnt.Records.Select(ProcessRecordAsync);
        await Task.WhenAll(tasks);
    }

    private Task ProcessRecordAsync(DynamoDBEvent.DynamodbStreamRecord record)
    {
        var pk = record.Dynamodb.Keys.TryGetValue("PK", out var pkAttr) ? pkAttr.S : null;
        if (pk is null) return Task.CompletedTask;

        if (pk == "METRIC#REQUEST" && record.EventName == "INSERT")
            return AddToGlobalCounterAsync("/system/metrics", "RequestCount", 1);

        if (pk == "TIME#/system/time" && record.Dynamodb.NewImage.TryGetValue("LastRequestTime", out var timeAttr))
            return ForwardLatestTimeAsync(timeAttr.S);

        // RATE#/UPLOAD# scratch items are regional-only and never forwarded.
        return Task.CompletedTask;
    }

    private Task AddToGlobalCounterAsync(string path, string attributeName, long delta)
        => _ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = GlobalTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = $"SYS#{path}" },
                ["SK"] = new() { S = "STATE" },
            },
            UpdateExpression = "ADD #c :delta SET Path = :path, LastWriteRegion = :region",
            ExpressionAttributeNames = new() { ["#c"] = attributeName },
            ExpressionAttributeValues = new()
            {
                { ":delta", new AttributeValue { N = delta.ToString() } },
                { ":path", new AttributeValue { S = path } },
                { ":region", new AttributeValue { S = Region } },
            },
        });

    private Task ForwardLatestTimeAsync(string isoTimestamp)
        => _ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = GlobalTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new() { S = "SYS#/system/time" },
                ["SK"] = new() { S = "STATE" },
            },
            UpdateExpression = "SET LastRequestTime = :t, #p = :path, LastWriteRegion = :region",
            ExpressionAttributeNames = new() { ["#p"] = "Path" },
            ExpressionAttributeValues = new()
            {
                { ":t", new AttributeValue { S = isoTimestamp } },
                { ":path", new AttributeValue { S = "/system/time" } },
                { ":region", new AttributeValue { S = Region } },
            },
        });

    private static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
