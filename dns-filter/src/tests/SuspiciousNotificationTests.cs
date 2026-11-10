using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using DnsFilterLambda;
using Moq;
using System.Text.Json;
using Xunit;

namespace test;

public class SuspiciousNotificationTests
{
    [Fact]
    public async Task OnlyNewSuspiciousDomainsEmitEventsWithTheirOwnReasons()
    {
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var events = new Mock<IAmazonEventBridge>();
        var sent = new List<PutEventsRequest>();
        events.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutEventsRequest, CancellationToken>((request, _) => sent.Add(request))
            .ReturnsAsync(new PutEventsResponse { FailedEntryCount = 0, Entries = [new() { EventId = "accepted" }] });
        var function = new DnsAnalysisLambda.Function(new Mock<IAmazonDynamoDB>().Object,
            new Mock<IAmazonS3>().Object, events.Object);
        var ratio = new DomainState { Domain = "ratio.example", TotalQueries = 10, NxDomainCount = 5 };
        var labels = new DomainState { Domain = "labels.example", MaxLength = 80 };
        var old = new DomainState { Domain = "old.example", IsSuspicious = true, TotalQueries = 10, NxDomainCount = 10 };
        await function.NotifyOfNewlySuspiciousDomains([ratio, labels, old, new() { Domain = "safe.example" }]);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, request => Assert.Equal("suspicious-domain", request.Entries.Single().DetailType));
        using var first = JsonDocument.Parse(sent[0].Entries[0].Detail);
        using var second = JsonDocument.Parse(sent[1].Entries[0].Detail);
        Assert.Contains("High NXDOMAIN ratio", first.RootElement.GetProperty("Reasons").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("High NXDOMAIN ratio", first.RootElement.GetProperty("ReasonSummary").GetString());
        Assert.Equal("/rules?kind=domain&value=ratio.example", first.RootElement.GetProperty("RulePath").GetString());
        Assert.DoesNotContain("High NXDOMAIN ratio", second.RootElement.GetProperty("Reasons").EnumerateArray().Select(x => x.GetString()));
        Assert.True(ratio.IsSuspicious);
        Assert.True(labels.IsSuspicious);
        await function.NotifyOfNewlySuspiciousDomains([ratio, labels, old]);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task EventEntryFailureDoesNotMarkDomainAsNotified()
    {
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var events = new Mock<IAmazonEventBridge>();
        events.Setup(x => x.PutEventsAsync(It.IsAny<PutEventsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutEventsResponse { FailedEntryCount = 1,
                Entries = [new() { ErrorCode = "InternalFailure", ErrorMessage = "retry" }] });
        var function = new DnsAnalysisLambda.Function(new Mock<IAmazonDynamoDB>().Object,
            new Mock<IAmazonS3>().Object, events.Object);
        var domain = new DomainState { Domain = "new.example", TotalQueries = 10, NxDomainCount = 10 };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => function.NotifyOfNewlySuspiciousDomains([domain]));
        Assert.Contains("InternalFailure", error.Message);
        Assert.False(domain.IsSuspicious);
    }

    private static Dictionary<string, AttributeValue> State(string domain, bool suspicious) => new()
    {
        ["PK"] = new(domain), ["SK"] = new("STATE"), ["TotalQueries"] = new() { N = "10" },
        ["NxDomainCount"] = new() { N = "5" }, ["Version"] = new() { N = "1" },
        ["HllState"] = new() { B = new MemoryStream(new HyperLogLogEstimator().Serialize()) },
        ["IsSuspicious"] = new() { BOOL = suspicious }
    };

    [Fact]
    public async Task SuspiciousFlagRoundTripsAndUnprocessedReadsAreRetried()
    {
        var db = new Mock<IAmazonDynamoDB>();
        var key = new Dictionary<string, AttributeValue> { ["PK"] = new("old.example"), ["SK"] = new("STATE") };
        db.SetupSequence(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse { Responses = new(),
                UnprocessedKeys = new() { ["table"] = new() { Keys = [key] } } })
            .ReturnsAsync(new BatchGetItemResponse { Responses = new() { ["table"] = [State("old.example", true)] } });
        db.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DomainStateStore(db.Object, "table");
        var states = await store.BatchGetAsync(new[] { "old.example", "new.example" });
        Assert.True(states[0]!.IsSuspicious);
        Assert.False(states[1]!.IsSuspicious);
        Assert.Equal("new.example", states[1]!.Domain);
        await store.BatchUpdateAsync(states.Select(x => x!).ToArray());
        db.Verify(x => x.TransactWriteItemsAsync(It.Is<TransactWriteItemsRequest>(r =>
            r.TransactItems[0].Update.UpdateExpression.Contains("IsSuspicious = :suspicious") &&
            r.TransactItems[0].Update.ExpressionAttributeValues[":suspicious"].BOOL == true),
            It.IsAny<CancellationToken>()), Times.Once);
        db.Verify(x => x.BatchGetItemAsync(It.Is<BatchGetItemRequest>(r =>
            r.RequestItems["table"].ConsistentRead == true), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExhaustedStateReadsFailInsteadOfSendingDuplicateNewAlerts()
    {
        var db = new Mock<IAmazonDynamoDB>();
        db.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BatchGetItemRequest request, CancellationToken _) =>
                new BatchGetItemResponse { Responses = new(), UnprocessedKeys = request.RequestItems });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DomainStateStore(db.Object, "table").BatchGetAsync(new[] { "old.example" }));
    }

    [Fact]
    public async Task StateBatchesRespectTheHundredItemReadAndTransactionLimits()
    {
        var db = new Mock<IAmazonDynamoDB>();
        db.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse { Responses = new() });
        db.Setup(x => x.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactWriteItemsResponse());
        var store = new DomainStateStore(db.Object, "table");
        var states = await store.BatchGetAsync(Enumerable.Range(0, 101).Select(i => $"domain{i}.example").ToArray());
        Assert.Equal(101, states.Length);
        await store.BatchUpdateAsync(states.Select(x => x!).ToArray());
        db.Verify(x => x.BatchGetItemAsync(It.Is<BatchGetItemRequest>(r => r.RequestItems["table"].Keys.Count <= 100),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        db.Verify(x => x.TransactWriteItemsAsync(It.Is<TransactWriteItemsRequest>(r => r.TransactItems.Count <= 100),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SingleStateReadsAndWritesPreserveSuspicionAndLegacyStatesDefaultToUnflagged()
    {
        var db = new Mock<IAmazonDynamoDB>();
        var legacy = State("legacy.example", false);
        legacy.Remove("IsSuspicious");
        db.Setup(x => x.GetItemAsync("table", It.IsAny<Dictionary<string, AttributeValue>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = State("old.example", true) });
        db.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateItemResponse());
        db.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse { Responses = new() { ["table"] = [legacy] } });
        var store = new DomainStateStore(db.Object, "table");
        var old = await store.GetAsync("old.example");
        Assert.True(old!.IsSuspicious);
        Assert.Equal("old.example", old.Domain);
        await store.UpdateAsync(old);
        db.Verify(x => x.UpdateItemAsync(It.Is<UpdateItemRequest>(r =>
            r.UpdateExpression.Contains("IsSuspicious = :suspicious") &&
            r.ExpressionAttributeValues[":suspicious"].BOOL == true), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False((await store.BatchGetAsync(new[] { "legacy.example" }))[0]!.IsSuspicious);
    }
}
