using System.IO.Compression;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.S3;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DnsAsnDataLambda;

public class Function(IAmazonDynamoDB ddb, IAmazonS3 s3, HttpClient http)
{
    private static readonly string TABLE_NAME = Environment.GetEnvironmentVariable(nameof(TABLE_NAME))
        ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set");
    private static readonly string BUCKET_NAME = Environment.GetEnvironmentVariable(nameof(BUCKET_NAME))
        ?? throw new InvalidOperationException($"{nameof(BUCKET_NAME)} environment variable is not set");

    public Function() : this(new AmazonDynamoDBClient(), new AmazonS3Client(), new HttpClient()) { }

    public async Task FunctionHandler(System.Text.Json.Nodes.JsonObject input, ILambdaContext context)
    {
        var cts = new CancellationTokenSource(context.RemainingTime.Add(TimeSpan.FromMilliseconds(-200)));
        context.Logger.LogInformation("FunctionHandler started");

        var blockedAsns = await GetBlockedAsnsAsync(cts.Token);
        var ranges = await GetBlockedAsnRanges(blockedAsns, cts.Token);

        context.Logger.LogInformation($"Retrieved {ranges.Count} ASN ranges for blocked ASNs");
        var asnData = AsnBlockWriter.ProcessWriteAsnData(ranges, cts.Token);

        context.Logger.LogInformation("ASN data processed, writing to S3");
        var response = await s3.PutObjectAsync(new()
        {
            BucketName = BUCKET_NAME,
            Key = "asn-from-ip-data.bin",
            InputStream = new MemoryStream(asnData.ToArray()),
            ContentType = "application/octet-stream"
        }, cts.Token);

        context.Logger.LogInformation($"S3 PutObject response: {response.HttpStatusCode}");
        context.Logger.LogInformation("FunctionHandler completed successfully");
    }

    internal async Task<HashSet<uint>> GetBlockedAsnsAsync(CancellationToken token)
    {
        var response = await ddb.QueryAsync(new()
        {
            TableName = Function.TABLE_NAME,
            IndexName = "GSI1",
            KeyConditionExpression = "GSI1PK = :pk",
            ExpressionAttributeValues = new()
            {
                [":pk"] = new() { S = "BLOCKED_ASNS" }
            },
            ProjectionExpression = "asn"
        }, token);
        if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Failed to query blocked ASNs: {response.HttpStatusCode}");
        }
        var blockedAsns = response.Items.Select(item =>
            item.TryGetValue("asn", out var asnAttr) && asnAttr.N is string asnStr && uint.TryParse(asnStr, out var asn)
                ? asn : 0u).Where(asn => asn != 0).ToHashSet();
        return blockedAsns;
    }

    internal async Task<List<AsnRange>> GetBlockedAsnRanges(HashSet<uint> blockedAsns, CancellationToken token)
    {
        const string url = "https://iptoasn.com/data/ip2asn-combined.tsv.gz";
        using var response = await http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        using var compressed = await response.Content.ReadAsStreamAsync(token);
        using var stream = new GZipStream(compressed, CompressionMode.Decompress);
        return await AsnRangeReader.ReadAsnRangesAsync(stream, blockedAsns, token);
    }
}