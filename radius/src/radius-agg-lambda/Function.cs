// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text.Json.Serialization;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: Amazon.Lambda.Core.LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<AuthAggregationLambda.JsonContext>))]

namespace AuthAggregationLambda;

public class Function(Amazon.S3.IAmazonS3 s3, Amazon.BedrockRuntime.IAmazonBedrockRuntime bedrock, Amazon.DynamoDBv2.IAmazonDynamoDB ddb)
{
    static readonly string RADIUS_AUTH_STATE_TABLE = Environment.GetEnvironmentVariable(nameof(RADIUS_AUTH_STATE_TABLE)) ?? throw new ArgumentException($"{nameof(RADIUS_AUTH_STATE_TABLE)} environment variable is required");
    static readonly string AWS_REGION = Environment.GetEnvironmentVariable(nameof(AWS_REGION)) ?? throw new ArgumentException($"{nameof(AWS_REGION)} environment variable is required");
    static readonly string BEDROCK_MODEL_ID = Environment.GetEnvironmentVariable(nameof(BEDROCK_MODEL_ID)) ?? throw new ArgumentException($"{nameof(BEDROCK_MODEL_ID)} environment variable is required");

    const string SYSTEM_PROMPT = "You are a RADIUS security expert. Given a summary of requests identify concerns. Answer concisely with only \"OK\" if no concerns are detected, otherwise identify the suspicious IP or calling station ID value. If more than one value is concerning, separate them on new lines.";
    const string USER_PROMPT = "remote_ip,called_station,calling_station,packet_code,count\n";

    public Function() : this(new Amazon.S3.AmazonS3Client(), new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient(), new Amazon.DynamoDBv2.AmazonDynamoDBClient()) { }

    public async Task<AggregationResponse> FunctionHandler(AggregationRequest request, Amazon.Lambda.Core.ILambdaContext context)
    {
        context.Logger.LogLine($"Received request to aggregate RADIUS auth logs from s3://{request.BucketName}/{request.ObjectKey}");

        // 1. read all lines from the s3 object, transform each JSON line into a light representation to use for inference and write
        // the results to another s3 object.
        //
        // 2. concurrently transform the input lines to a dictionary of remote IPs to counts, and calling station IDs to counts.
        //
        var ip_counts = new Dictionary<string, ulong>();
        var calling_station_counts = new Dictionary<string, ulong>();
        var called_station_counts = new Dictionary<string, ulong>();
        var bedrock_input = new Dictionary<string, ulong>();

        var tu = new Amazon.S3.Transfer.TransferUtility(s3);
        using var stream = await tu.OpenStreamAsync(request.BucketName, request.ObjectKey);
        using var decompress = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(decompress);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var doc = System.Text.Json.JsonDocument.Parse(line);
            var remote_ip = (doc.RootElement.TryGetProperty("Remote", out var r1) && r1.TryGetProperty("IpAddress", out var ip) ? ip.GetString() : null) ?? "-";
            var remote_port = doc.RootElement.TryGetProperty("Remote", out var r2) && r2.TryGetProperty("Port", out var p) ? p.GetInt32() : 0;
            var data_string = (doc.RootElement.TryGetProperty("Data", out var d) ? d.GetString() : null) ?? "";
            var data = Convert.FromBase64String(data_string);

            var (code, length, id, authenticator, attributes) = RadiusAcctLambda.RadiusPacketReader.Parse(data);
            var calling = attributes.FirstOrDefault(a => a.type == 31).value;
            var calling_hex = calling.IsEmpty ? "-" : BitConverter.ToString(calling.ToArray()).Replace("-", "");

            var called = attributes.FirstOrDefault(a => a.type == 30).value;
            var called_hex = called.IsEmpty ? "-" : BitConverter.ToString(called.ToArray()).Replace("-", "");

            ip_counts[remote_ip] = ip_counts.TryGetValue(remote_ip, out ulong ip_value) ? ip_value + 1 : 1;
            calling_station_counts[calling_hex] = calling_station_counts.TryGetValue(calling_hex, out ulong calling_value) ? calling_value + 1 : 1;
            called_station_counts[called_hex] = called_station_counts.TryGetValue(called_hex, out ulong called_value) ? called_value + 1 : 1;

            var key = $"{remote_ip},{called_hex},{calling_hex},{code}";
            bedrock_input[key] = bedrock_input.TryGetValue(key, out ulong value) ? value + 1 : 1;
        }

        // 3. start call to bedrock inference endpoint (use nova light) prompt
        var prompt = USER_PROMPT + string.Join('\n', bedrock_input.Select(kv => $"{kv.Key},{kv.Value}")) + "\n\nAnswer: ";
        context.Logger.LogLine($"Calling bedrock with prompt ({prompt.Length} characters):\n{prompt}");

        var bedrock_task = bedrock.ConverseAsync(new Amazon.BedrockRuntime.Model.ConverseRequest
        {
            ModelId = BEDROCK_MODEL_ID,
            System = [
                new() { Text = SYSTEM_PROMPT }
            ],
            Messages = [
                new() { Role = "user", Content = [ new() { Text = prompt } ] }
            ],
            InferenceConfig = new()
            {
                MaxTokens = 512,
                Temperature = 0.1f
            }
        });

        // 4. Update DDB tables with the aggregated counts.
        var now = DateTime.UtcNow;
        var ip_updates = ip_counts.Select(kv => new Amazon.DynamoDBv2.Model.Update()
        {
            TableName = RADIUS_AUTH_STATE_TABLE,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["PK"] = new() { S = $"IP#{kv.Key}" },
                ["SK"] = new() { S = $"{now:yyyy-MM-ddTHH}" }
            },
            UpdateExpression = "SET #count = if_not_exists(#count, :zero) + :inc, last_updated = :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#count"] = "count" },
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":inc"] = new() { N = kv.Value.ToString() },
                [":zero"] = new() { N = "0" },
                [":now"] = new() { S = now.ToString("o") }
            }
        });
        var cs_updates = calling_station_counts.Select(kv => new Amazon.DynamoDBv2.Model.Update()
        {
            TableName = RADIUS_AUTH_STATE_TABLE,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["PK"] = new() { S = $"CS#{kv.Key}" },
                ["SK"] = new() { S = $"{now:yyyy-MM-ddTHH}" }
            },
            UpdateExpression = "SET #count = if_not_exists(#count, :zero) + :inc, last_updated = :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#count"] = "count" },
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":inc"] = new() { N = kv.Value.ToString() },
                [":zero"] = new() { N = "0" },
                [":now"] = new() { S = now.ToString("o") }
            }
        });
        var update_batches = ip_updates.Concat(cs_updates).Chunk(25); // DDB batch write limit is 25 items
        var ddb_tasks = update_batches.Select(batch => ddb.TransactWriteItemsAsync(new Amazon.DynamoDBv2.Model.TransactWriteItemsRequest
        {
            TransactItems = [.. batch.Select(u => new Amazon.DynamoDBv2.Model.TransactWriteItem { Update = u })]
        }));

        // 5. When bedrock results are available, update DDB with any detected anomalies.
        var bedrock_response = await bedrock_task;
        var anomalies = bedrock_response.Output?.Message?.Content?.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t) && t != "OK") ?? [];

        context.Logger.LogLine($"Bedrock response: {string.Join("\n", anomalies)}");
        // TODO: parse anomalies and write to DDB table
        
        await Task.WhenAll(ddb_tasks);

        return await Task.FromResult(new AggregationResponse());
    }
}

public class AggregationRequest
{
    public string BucketName { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
}

public class AggregationResponse { }

[JsonSerializable(typeof(AggregationRequest))]
[JsonSerializable(typeof(AggregationResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class JsonContext : JsonSerializerContext
{
}
