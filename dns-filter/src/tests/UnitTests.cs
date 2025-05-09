using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Moq;
using Xunit;

namespace test;

public class UnitTests
{
    [Fact]
    public void FunctionConstructs()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var function = new DnsFilterLambda.Function(ddb.Object);
        Assert.NotNull(function);
    }

    [Fact]
    public void FunctionHandlerReturnsJsonObjectWithRepliesArray()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var function = new DnsFilterLambda.Function(ddb.Object);
        var input = new JsonObject { ["Messages"] = new JsonArray() };
        var context = new Mock<ILambdaContext>();
        
        var result = function.FunctionHandler(input, context.Object).Result;
        
        Assert.IsType<JsonObject>(result);
        Assert.True(result.ContainsKey("Replies"));
        Assert.IsType<JsonArray>(result["Replies"]);
    }

    [Fact]
    public void FunctionHandlerReturnsRepliesWithMatchingTags()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });

        var request = new DNS.Protocol.Request();
        request.Questions.Add(new DNS.Protocol.Question(DNS.Protocol.Domain.FromString("example.com"), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN));
        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = Convert.ToBase64String(request.ToArray()) },
            new JsonObject { ["Tag"] = "tag3", ["Data"] = Convert.ToBase64String(request.ToArray()) },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();
        
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var function = new DnsFilterLambda.Function(ddb.Object);
        var result = function.FunctionHandler(input, context.Object).Result;
        
        var replies = (JsonArray)result["Replies"]!;
        foreach (var reply in replies)
        {
            Assert.IsType<JsonObject>(reply);
            Assert.True(reply.AsObject().ContainsKey("Tag"));
            Assert.IsAssignableFrom<JsonValue>(reply["Tag"]);
        }
        Assert.Equal(2, replies.Count);
        Assert.Equal("tag1", replies[0]!["Tag"]!.ToString());
        Assert.Equal("tag3", replies[1]!["Tag"]!.ToString());
    }

    [Fact]
    public void FunctionHandlerCachesBlockedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var blocked = new HashSet<string> { "example.com" };
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = blocked.Contains(request.Key["PK"].S) ? new() {
                    ["PK"] = new() { S = request.Key["PK"].S },
                    ["SK"] = new() { S = request.Key["SK"].S },
                    ["blocked"] = new() { BOOL = true }
                } : new()
            }));

        var request = new DNS.Protocol.Request();
        request.Questions.Add(new DNS.Protocol.Question(DNS.Protocol.Domain.FromString("example.com"), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN));
        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = Convert.ToBase64String(request.ToArray()) },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();
        
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var function = new DnsFilterLambda.Function(ddb.Object);

        var result1 = function.FunctionHandler(input, context.Object).Result;
        var result2 = function.FunctionHandler(input, context.Object).Result;

        // called once for each part of the domain, but only for the first request
        ddb.Verify(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));        
    }

    [Fact]
    public void FunctionHandlerReturnsBlockedIpForBlockedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var blocked = new HashSet<string> { "example.com" };
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = blocked.Contains(request.Key["PK"].S) ? new() {
                    ["PK"] = new() { S = request.Key["PK"].S },
                    ["SK"] = new() { S = request.Key["SK"].S },
                    ["blocked"] = new() { BOOL = true }
                } : new()
            }));

        var request = new DNS.Protocol.Request();
        request.Questions.Add(new DNS.Protocol.Question(DNS.Protocol.Domain.FromString("example.com"), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN));
        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = Convert.ToBase64String(request.ToArray()) },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();
        
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var function = new DnsFilterLambda.Function(ddb.Object);

        var result1 = function.FunctionHandler(input, context.Object).Result;
        var result2 = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result1["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);
        Assert.Single(response.AnswerRecords);
        Assert.Equal(DNS.Protocol.RecordType.A, response.AnswerRecords[0].Type);
        Assert.Equal(DNS.Protocol.RecordClass.IN, response.AnswerRecords[0].Class);
        Assert.Equal([ 0, 0, 0, 0 ], response.AnswerRecords[0].Data);
    }

    [Fact]
    public void FunctionHandlerReturnsRedirectIpForRedirectedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var redirected = new Dictionary<string, string> { ["example.com"] = "3.4.5.6" };
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = redirected.ContainsKey(request.Key["PK"].S) ? new() {
                    ["PK"] = new() { S = request.Key["PK"].S },
                    ["SK"] = new() { S = request.Key["SK"].S },
                    ["redirect"] = new() { S = redirected[request.Key["PK"].S] }
                } : new()
            }));
        
        var request = new DNS.Protocol.Request();
        request.Questions.Add(new DNS.Protocol.Question(DNS.Protocol.Domain.FromString("example.com"), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN));
        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = Convert.ToBase64String(request.ToArray()) },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();

        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var function = new DnsFilterLambda.Function(ddb.Object);

        var result = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);
        Assert.Single(response.AnswerRecords);
        Assert.Equal(DNS.Protocol.RecordType.A, response.AnswerRecords[0].Type);
        Assert.Equal(DNS.Protocol.RecordClass.IN, response.AnswerRecords[0].Class);
        Assert.Equal([ 3, 4, 5, 6 ], response.AnswerRecords[0].Data);
    }

    [Fact]
    public void FunctionHandlerReturnsResolvedIpsForUnblockedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });

        var request = new DNS.Protocol.Request();
        request.Questions.Add(new DNS.Protocol.Question(DNS.Protocol.Domain.FromString("example.com"), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN));
        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = Convert.ToBase64String(request.ToArray()) },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();
        
        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        var function = new DnsFilterLambda.Function(ddb.Object);

        var result = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);
        // verify the response contains public IP addressses for example.com
        Assert.NotEmpty(response.AnswerRecords);
        Assert.All(response.AnswerRecords, record =>
        {
            Assert.Equal(DNS.Protocol.RecordType.A, record.Type);
            Assert.Equal(DNS.Protocol.RecordClass.IN, record.Class);
            Assert.NotEqual([ 0, 0, 0, 0 ], record.Data);
        });
    }
}