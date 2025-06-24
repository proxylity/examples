using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KinesisFirehose;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;
using Xunit;

namespace test;

public class UnitTests
{
    [Fact]
    public void FunctionConstructs()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);
        Assert.NotNull(function);
    }

    [Fact]
    public void FunctionHandlerReturnsJsonObjectWithRepliesArray()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
            });

        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);
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
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);
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
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<BatchGetItemRequest, CancellationToken>((request, token) => Task.FromResult(new BatchGetItemResponse
            {
                Responses = request.RequestItems.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Keys
                        .Where(key => blocked.Contains(key["PK"].S))
                        .Select(key => new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = new() { S = key["PK"].S },
                            ["SK"] = new() { S = key["SK"].S },
                            ["blocked"] = new() { BOOL = true }
                        })
                        .ToList()
                )
            }));
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key["PK"].S.StartsWith("AS#") ? [] : []
            }));
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result1 = function.FunctionHandler(input, context.Object).Result;
        var result2 = function.FunctionHandler(input, context.Object).Result;

        // called once with each part of the domain, but only for the first request
        ddb.Verify(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(1));
        // called one for the ASN lookup, but only for the first request
        s3.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(1));
    }

    [Fact]
    public void FunctionHandlerReturnsBlockedIpForBlockedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var blocked = new HashSet<string> { "example.com" };
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<BatchGetItemRequest, CancellationToken>((request, token) => Task.FromResult(new BatchGetItemResponse
            {
                Responses = request.RequestItems.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Keys
                        .Where(key => blocked.Contains(key["PK"].S))
                        .Select(key => new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = new() { S = key["PK"].S },
                            ["SK"] = new() { S = key["SK"].S },
                            ["blocked"] = new() { BOOL = true }
                        })
                        .ToList()
                )
            }));
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key["PK"].S.StartsWith("AS#") ? [] : []
            }));
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result1 = function.FunctionHandler(input, context.Object).Result;
        var result2 = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result1["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);

        Assert.Equal(DNS.Protocol.ResponseCode.NameError, response.ResponseCode);
        Assert.Empty(response.AnswerRecords);
    }

    [Fact]
    public void FunctionHandlerReturnsRedirectIpForRedirectedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        var redirected = new Dictionary<string, string> { ["example.com"] = "3.4.5.6" };
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<BatchGetItemRequest, CancellationToken>((request, token) => Task.FromResult(new BatchGetItemResponse
            {
                Responses = request.RequestItems.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Keys
                        .Where(key => redirected.ContainsKey(key["PK"].S))
                        .Select(key => new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = new() { S = key["PK"].S },
                            ["SK"] = new() { S = key["SK"].S },
                            ["redirect"] = new() { S = redirected[key["PK"].S] }
                        })
                        .ToList()
                )
            }));
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetItemRequest, CancellationToken>((request, token) => Task.FromResult(new GetItemResponse
            {
                Item = request.Key["PK"].S.StartsWith("AS#") ? [] : []
            }));
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);
        Assert.Single(response.AnswerRecords);
        Assert.Equal(DNS.Protocol.RecordType.A, response.AnswerRecords[0].Type);
        Assert.Equal(DNS.Protocol.RecordClass.IN, response.AnswerRecords[0].Class);
        Assert.Equal([3, 4, 5, 6], response.AnswerRecords[0].Data);
    }

    [Fact]
    public void FunctionHandlerReturnsResolvedIpsForUnblockedDomains()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = new() {
                    { "test-table", [] }
                }
            });
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

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
            Assert.NotEqual([0, 0, 0, 0], record.Data);
        });
    }

    [Fact]
    public async Task FunctionHandlerLogsQueriesWhenNotDisabled()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = new() {
                    { "test-table", [] }
                }
            });
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
            });


        var loglines = new List<string>();
        firehose.Setup(x => x.PutRecordAsync(It.IsAny<Amazon.KinesisFirehose.Model.PutRecordRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Amazon.KinesisFirehose.Model.PutRecordRequest, CancellationToken>((r, t) =>
            {
                var line = System.Text.Encoding.UTF8.GetString(r.Record.Data.ToArray());
                loglines.Add(line);
                Console.Write(line);
            })
            .ReturnsAsync(new Amazon.KinesisFirehose.Model.PutRecordResponse
            {
                RecordId = "test-record-id"
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        Environment.SetEnvironmentVariable("LOG_FIREHOSE_STREAM", "test-firehose-stream");
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result = await function.FunctionHandler(input, context.Object);

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);
        // verify the response contains public IP addressses for example.com
        Assert.NotEmpty(response.AnswerRecords);
        Assert.All(response.AnswerRecords, record =>
        {
            var iphex = Convert.ToHexString(record.Data);
            Console.WriteLine($"Domain: {record.Name}, IP: {iphex}");
            Assert.Contains(loglines, line => line.Contains("example.com"));
            Assert.Contains(loglines, line => line.Contains(iphex));
        });
        Console.Write($"Log lines: {string.Join("", loglines)}");
    }

    [Fact]
    public void FunctionHandlerDoesNotLogQueriesWhenDisabled()
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = new() {
                    { "test-table", [] }
                }
            });
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
            });

        var loglines = new List<string>();
        firehose.Setup(x => x.PutRecordAsync(It.IsAny<Amazon.KinesisFirehose.Model.PutRecordRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Amazon.KinesisFirehose.Model.PutRecordRequest, CancellationToken>((r, t) => { loglines.Add(System.Text.Encoding.UTF8.GetString(r.Record.Data.ToArray())); })
            .ReturnsAsync(new Amazon.KinesisFirehose.Model.PutRecordResponse
            {
                RecordId = "test-record-id"
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
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        Environment.SetEnvironmentVariable("LOG_FIREHOSE_STREAM", string.Empty);
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);

        Assert.NotEmpty(response.AnswerRecords);
        Assert.Empty(loglines);
    }


    [Theory]
    [InlineData("6wcBIAABAAAAAAABCGV4YW1wbGUyA2NvbQAAAgABAAApBNAAAAAAAAwACgAIL7ut8DxFhLI=")]
    [InlineData("AAABAAABAAAAAAABA2xoMxFnb29nbGV1c2VyY29udGVudANjb20AABwAAQAAKRAAAAAAAABKAAgABAABAAAADAA+AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void PacketExamples(string base64Packet)
    {
        Mock<IAmazonDynamoDB> ddb = new();
        ddb.Setup(x => x.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = new() {
                    { "test-table", [] }
                }
            });
        ddb.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = []
            });
        Mock<IAmazonKinesisFirehose> firehose = new();
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse
            {
                ResponseStream = new MemoryStream()
            });


        var loglines = new List<string>();
        firehose.Setup(x => x.PutRecordAsync(It.IsAny<Amazon.KinesisFirehose.Model.PutRecordRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Amazon.KinesisFirehose.Model.PutRecordRequest, CancellationToken>((r, t) => { loglines.Add(System.Text.Encoding.UTF8.GetString(r.Record.Data.ToArray())); })
            .ReturnsAsync(new Amazon.KinesisFirehose.Model.PutRecordResponse
            {
                RecordId = "test-record-id"
            });

        var requests = new JsonArray
        {
            new JsonObject { ["Tag"] = "tag1", ["Data"] = base64Packet },
        };
        var input = new JsonObject { ["Messages"] = requests };
        var context = new Mock<ILambdaContext>();

        Environment.SetEnvironmentVariable("TABLE_NAME", "test-table");
        Environment.SetEnvironmentVariable("BUCKET_NAME", "test-bucket");
        Environment.SetEnvironmentVariable("LOG_FIREHOSE_STREAM", string.Empty);
        var function = new DnsFilterLambda.Function(ddb.Object, firehose.Object, s3.Object);

        var result = function.FunctionHandler(input, context.Object).Result;

        var replies = (JsonArray)result["Replies"]!;
        var reply_data = Convert.FromBase64String(replies[0]!["Data"]!.ToString()!);
        var response = DNS.Protocol.Response.FromArray(reply_data);

        Assert.NotEmpty(response.AnswerRecords);
        Assert.Empty(loglines);
    }
}