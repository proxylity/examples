using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KinesisFirehose;
using Amazon.S3;
using DnsFilterLambda;
using Moq;
using Xunit;

namespace test;

public class IpPolicyTests
{
    [Theory]
    [InlineData("192.0.2.129", 33, "IP#192.0.2.0/24", "IP#0.0.0.0/0")]
    [InlineData("2001:db8::abcd", 129, "IP#2001:db8::/64", "IP#::/0")]
    public void GeneratesCanonicalPrefixKeys(string ip, int count, string network, string last)
    {
        var keys = NetworkKeys.For(IPAddress.Parse(ip)).ToArray();
        Assert.Equal(count, keys.Length);
        Assert.Contains(network, keys);
        Assert.Equal(last, keys[^1]);
        Assert.Equal(count, keys.Distinct().Count());
    }

    [Fact]
    public async Task SharedIpPrefixesAreBatchedAndSpecificAllowStopsBroaderBlock()
    {
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var db = new Mock<IAmazonDynamoDB>();
        var calls = 0;
        db.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<BatchGetItemRequest, CancellationToken>((request, _) =>
            {
                calls++;
                var table = request.RequestItems.Single();
                return Task.FromResult(new BatchGetItemResponse { Responses = new() { [table.Key] = [
                    new() { ["PK"] = new() { S = "IP#192.0.2.0/24" }, ["blocked"] = new() { BOOL = true } },
                    new() { ["PK"] = new() { S = "IP#192.0.2.1/32" }, ["allowed"] = new() { BOOL = true } }
                ] } });
            });
        var function = new Function(db.Object, new Mock<IAmazonKinesisFirehose>().Object, new Mock<IAmazonS3>().Object);
        var policies = await function.GetIpInfos([IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")]);
        Assert.False(policies[IPAddress.Parse("192.0.2.1")].blocked);
        Assert.True(policies[IPAddress.Parse("192.0.2.2")].blocked);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IpLookupBatchesIpv6RetriesUnprocessedAndCachesResults()
    {
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var db = new Mock<IAmazonDynamoDB>();
        var requests = new List<BatchGetItemRequest>();
        db.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<BatchGetItemRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                var table = request.RequestItems.Single();
                Assert.True(table.Value.Keys.Count <= 100);
                if (requests.Count == 1) return Task.FromResult(new BatchGetItemResponse { UnprocessedKeys = request.RequestItems });
                return Task.FromResult(new BatchGetItemResponse { Responses = new() { [table.Key] = table.Value.Keys
                    .Where(k => k["PK"].S == "IP#2001:db8::/64")
                    .Select(k => new Dictionary<string, AttributeValue>(k) { ["blocked"] = new() { BOOL = true } }).ToList() } });
            });
        var function = new Function(db.Object, new Mock<IAmazonKinesisFirehose>().Object, new Mock<IAmazonS3>().Object);
        Assert.True((await function.GetIpInfo(IPAddress.Parse("2001:db8::1"))).blocked);
        Assert.Equal(3, requests.Count);
        Assert.True((await function.GetIpInfo(IPAddress.Parse("2001:db8::1"))).blocked);
        Assert.Equal(3, requests.Count);
    }
}
