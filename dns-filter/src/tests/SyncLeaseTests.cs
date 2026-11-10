using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DnsBlocklistSync;
using Moq;
using Xunit;

namespace test;

public class SyncLeaseTests
{
    [Fact]
    public async Task PolicyWritesRunConcurrentlyWithLeaseRenewal()
    {
        var db = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (UpdateItemRequest request, CancellationToken token) =>
            {
                if (request.Key["PK"].S == "SYNC#LEASE")
                    Assert.Contains("#owner = :owner", request.ConditionExpression);
                else
                    Assert.Equal("attribute_not_exists(PK) OR #managed = :sync", request.ConditionExpression);
                if (Interlocked.Increment(ref entered) == 17) allEntered.SetResult();
                await allEntered.Task.WaitAsync(token);
                return new UpdateItemResponse();
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new DynamoStore(db.Object, "table");
        var writes = Enumerable.Range(0, 16).Select(i =>
            store.Apply(new($"domain{i}.example", "feed", true), timeout.Token)).ToArray();
        await Task.WhenAll(writes.Cast<Task>().Append(store.Renew(timeout.Token)));
        Assert.Equal(17, entered);
        foreach (var write in writes) Assert.Equal(WriteResult.Applied, await write);
    }

    [Fact]
    public async Task ThrottlingRetriesButPermanentErrorsPropagate()
    {
        var db = new Mock<IAmazonDynamoDB>();
        db.SetupSequence(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"))
            .ReturnsAsync(new UpdateItemResponse())
            .ThrowsAsync(new AmazonDynamoDBException("denied") { ErrorCode = "AccessDeniedException" });
        var store = new DynamoStore(db.Object, "table");
        Assert.Equal(WriteResult.Applied, await store.Apply(new("one.example", "feed", true), default));
        await Assert.ThrowsAsync<AmazonDynamoDBException>(() => store.Apply(new("two.example", "feed", true), default));
        db.Verify(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RemovalAndCleanupPreserveOtherSourcesAndCustomerOwnership()
    {
        var db = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        db.Setup(x => x.UpdateItemAsync(It.Is<UpdateItemRequest>(r =>
            r.ConditionExpression == "#managed = :sync" && r.UpdateExpression == "DELETE sourceLists :list"),
            It.IsAny<CancellationToken>())).ReturnsAsync(new UpdateItemResponse());
        db.Setup(x => x.DeleteItemAsync(It.Is<DeleteItemRequest>(r =>
            r.ConditionExpression == "managedBy = :sync AND attribute_not_exists(sourceLists)"),
            It.IsAny<CancellationToken>())).ThrowsAsync(new ConditionalCheckFailedException("membership remains"));
        var store = new DynamoStore(db.Object, "table");
        Assert.Equal(WriteResult.Applied, await store.Apply(new("one.example", "feed", false), default));
        db.VerifyAll();
    }
}
