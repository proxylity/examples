using System.Net;
using System.Text;
using DnsAsnDataLambda;
using DnsFilterLambda;
using Xunit;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Moq;

namespace test;

public class AsnEncodingTests
{
    [Fact]
    public async Task AsnRulesAreReadAcrossAllIndexPages()
    {
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var db = new Mock<IAmazonDynamoDB>();
        db.SetupSequence(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { HttpStatusCode = HttpStatusCode.OK,
                Items = [new() { ["asn"] = new() { N = "42" } }],
                LastEvaluatedKey = new() { ["PK"] = new() { S = "AS#42" } } })
            .ReturnsAsync(new QueryResponse { HttpStatusCode = HttpStatusCode.OK,
                Items = [new() { ["asn"] = new() { N = "43" } }] });
        using var http = new HttpClient();
        var function = new DnsAsnDataLambda.Function(db.Object, new Mock<IAmazonS3>().Object, http);
        Assert.Equal(new uint[] { 42, 43 }, (await function.GetBlockedAsnsAsync(default)).Order().ToArray());
        db.Verify(x => x.QueryAsync(It.Is<QueryRequest>(r => r.ExclusiveStartKey != null &&
            r.ExclusiveStartKey.ContainsKey("PK")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PipelineRoundTripsIpv4AndHighBitIpv6Ranges()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "192.0.2.0\t192.0.2.255\t42\n8000::\t8000::ffff\t43\n"));
        var ranges = await AsnRangeReader.ReadAsnRangesAsync(input, [42, 43]);
        var lookup = new AsnBlockLookup(AsnBlockWriter.ProcessWriteAsnData(ranges));
        Assert.Equal(42u, lookup.Lookup(IPAddress.Parse("192.0.2.1")));
        Assert.Equal(42u, lookup.Lookup(IPAddress.Parse("192.0.2.255")));
        Assert.Equal(43u, lookup.Lookup(IPAddress.Parse("8000::1")));
        Assert.Null(lookup.Lookup(IPAddress.Parse("192.0.3.0")));
        Assert.Null(lookup.Lookup(IPAddress.Parse("8000::1:0")));
    }
}
