using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using System.Text.Json.Nodes;
using System.Net;
using Amazon.DynamoDBv2.Model;
using Amazon.KinesisFirehose;
using System.Net.Sockets;
using System.Data;
using Amazon.S3;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DnsFilterLambda
{
  public class Function(IAmazonDynamoDB ddb, IAmazonKinesisFirehose firehose, IAmazonS3 s3)
  {
    public static readonly string TABLE_NAME = Environment.GetEnvironmentVariable(nameof(TABLE_NAME))
      ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set");
    public static readonly string BUCKET_NAME = Environment.GetEnvironmentVariable(nameof(BUCKET_NAME))
      ?? throw new InvalidOperationException($"{nameof(BUCKET_NAME)} environment variable is not set");

    public static readonly IPAddress UPSTREAM_DNS = IPAddress.Parse(Environment.GetEnvironmentVariable("UPSTREAM_DNS") ?? "1.0.0.3");
    public static readonly TimeSpan FILTERED_RECORD_TTL = TimeSpan.FromSeconds(300);

    readonly string? LOG_FIREHOSE_STREAM = Environment.GetEnvironmentVariable("LOG_FIREHOSE_STREAM");

    readonly Dictionary<string, (bool blocked, IPAddress? to, DateTime expires)> _cache = [];
    readonly AsnLookupService _asnLookup = new(s3);
    static readonly DNS.Client.DnsClient _upstreamClient = new(UPSTREAM_DNS);

    public Function() : this(new AmazonDynamoDBClient(), new AmazonKinesisFirehoseClient(), new AmazonS3Client()) { }

    public async Task<JsonObject> FunctionHandler(JsonObject input, ILambdaContext context)
    {
      await Console.Out.WriteLineAsync($"Received event: {input.ToJsonString()}");

      await _asnLookup.InitializeAsync();

      var packets = input["Messages"]?.AsArray() ?? throw new InvalidOperationException("Invalid payload (not from UDP Gateway?)");

      var noop = Task.FromResult(new JsonObject());
      var reply_tasks = packets?.Select(p =>
      {
        var tag = p!["Tag"]?.ToString();
        var data = p["Data"]?.ToString();
        return tag is null || data is null ? noop
          : HandleDnsPacket(tag, Convert.FromBase64String(data));
      }) ?? [];

      var replies = await Task.WhenAll(reply_tasks);
      return new() { ["Replies"] = new JsonArray(replies) };
    }

    public async Task<JsonObject> HandleDnsPacket(string tag, byte[] data)
    {
      if (TryParseDnsRequest(data, out var request) && request is not null)
      {
        try
        {
          var response = DNS.Protocol.Response.FromRequest(request);

          await Console.Out.WriteLineAsync($"Processing DNS request {request}");

          var question = request.Questions[0];
          var domain = question.Name.ToString().TrimEnd('.');
          var (blocked, to) = await GetDomainInfo(domain);

          if (blocked)
          {
            response.ResponseCode = DNS.Protocol.ResponseCode.NameError;
            // no answer record
          }
          else if (to is not null)
          {
            var answer = MakeRedirectRecord(question.Type, question.Name, to);
            // if the to IP type doesn't match the question type (or is otherwise unusable), we don't add it
            if (answer is not null) response.AnswerRecords.Add(answer);
          }
          else
          {
            // Proxy upsstream and filter answers
            var upstream = await ProxyUpstreamAsync(question);
            // copy everything from the upstream response to our response
            if (upstream is not null)
            {
              foreach (var r in upstream.AnswerRecords) response.AnswerRecords.Add(r switch
              {
                DNS.Protocol.ResourceRecords.IPAddressResourceRecord =>
                  new DNS.Protocol.ResourceRecords.IPAddressResourceRecord(r.Name, new IPAddress(r.Data), r.TimeToLive),
                DNS.Protocol.ResourceRecords.CanonicalNameResourceRecord cr =>
                  new DNS.Protocol.ResourceRecords.CanonicalNameResourceRecord(cr.Name, cr.CanonicalDomainName, cr.TimeToLive),
                DNS.Protocol.ResourceRecords.MailExchangeResourceRecord mr =>
                  new DNS.Protocol.ResourceRecords.MailExchangeResourceRecord(mr.Name, mr.Preference, mr.ExchangeDomainName, mr.TimeToLive),
                DNS.Protocol.ResourceRecords.TextResourceRecord tr =>
                  new DNS.Protocol.ResourceRecords.TextResourceRecord(tr.Name, tr.TextData, tr.TimeToLive),
                DNS.Protocol.ResourceRecords.ServiceResourceRecord sr =>
                  new DNS.Protocol.ResourceRecords.ServiceResourceRecord(sr.Name, sr.Priority, sr.Weight, sr.Port, sr.Target, sr.TimeToLive),
                DNS.Protocol.ResourceRecords.NameServerResourceRecord nr =>
                  new DNS.Protocol.ResourceRecords.NameServerResourceRecord(nr.Name, nr.NSDomainName, nr.TimeToLive),
                _ => new DNS.Protocol.ResourceRecords.ResourceRecord(r.Name, r.Data, r.Type, r.Class, r.TimeToLive)
              });
              foreach (var r in upstream.AuthorityRecords) response.AuthorityRecords.Add(r);
              foreach (var r in upstream.AdditionalRecords) response.AdditionalRecords.Add(r);
              response.ResponseCode = upstream.ResponseCode;
              response.AuthenticData = upstream.AuthenticData;
              response.Truncated = upstream.Truncated;
              response.RecursionAvailable = upstream.RecursionAvailable;
            }
            else
            {
              response.ResponseCode = DNS.Protocol.ResponseCode.ServerFailure;
              await Console.Error.WriteLineAsync($"Failed to resolve {question.Name} upstream");
            }
          }

          // Process answer records for ASN filtering
          var addressRecordsByBlocked = response?.AnswerRecords
            .Where(a => a.Type is DNS.Protocol.RecordType.A or DNS.Protocol.RecordType.AAAA)
            .ToLookup(a => GetAsnInfo(new IPAddress(a.Data)))
            .ToArray() ?? [];

          foreach (var g in addressRecordsByBlocked)
          {
            var (asnBlocked, asnTo) = await g.Key;
            if (asnBlocked)
            {
              foreach (var a in g)
              {
                // Replace with block record
                var blockRecord = MakeBlockedAsnIpRecord(a.Type, a.Name);
                response!.AnswerRecords.Remove(a);
                response.AnswerRecords.Add(blockRecord);
              }
            }
            else if (asnTo is not null)
            {
              foreach (var a in g)
              {
                // Replace with redirect record
                var redirectRecord = MakeRedirectRecord(a.Type, a.Name, asnTo);
                if (redirectRecord is not null)
                {
                  response!.AnswerRecords.Remove(a);
                  response.AnswerRecords.Add(redirectRecord);
                }
              }
            }
          }

          await Console.Out.WriteLineAsync($"Produced response: {response}");
          await LogDnsQueryAsync(request, response!);

          return new()
          {
            ["Tag"] = tag,
            ["Data"] = Convert.ToBase64String(response!.ToArray())
          };
        }
        catch (Exception e)
        {
          await Console.Error.WriteLineAsync($"Unexpected {e.GetType().Name} error processing DNS packet: {e.Message}");
          return new()
          {
            ["Tag"] = tag,
            ["Data"] = Convert.ToBase64String(new DNS.Protocol.Response
            {
              Id = request.Id,
              ResponseCode = DNS.Protocol.ResponseCode.ServerFailure,
            }.ToArray())
          };
        }
      }

      await Console.Error.WriteLineAsync($"Failed to parse DNS request from data: {Convert.ToHexString(data)}");
      return new()
      {
        ["Tag"] = tag,
        ["Data"] = Convert.ToBase64String(new DNS.Protocol.Response
          {
            // ID is going to be zero since we don't have one from the request
            ResponseCode = DNS.Protocol.ResponseCode.ServerFailure
          }.ToArray()
        )
      };
    }

    private static bool TryParseDnsRequest(byte[] data, out DNS.Protocol.IRequest? request)
    {
      try
      {
        request = DNS.Protocol.Request.FromArray(data);
        return true;
      }
      catch (Exception e)
      {
        Console.Error.WriteLine($"Error parsing DNS request: {e.Message}");
        request = null;
        return false;
      }
    }

    private async Task LogDnsQueryAsync(DNS.Protocol.IRequest request, DNS.Protocol.IResponse response)
    {
      if (LOG_FIREHOSE_STREAM is null or "" || response.Questions.Count < 1) return;

      // log line format is: <ISO 8661 time in UTC>\tdomain\tqtype\tresponse_code\t[answer1,answer2,answer3...]
      // not that the answers (zero or more) are comma-separated, and each answer is in the format: type <data in hex>
      var question = request.Questions[0];
      var answers = string.Join(',', response.AnswerRecords.Select(r => $"{r.Type} {Convert.ToHexString(r.Data)}"));
      var logline = $"{DateTime.UtcNow:o}\t{question.Name}\t{question.Type}\t{response.ResponseCode}\t{answers}\n";

      await firehose.PutRecordAsync(new()
      {
        DeliveryStreamName = LOG_FIREHOSE_STREAM,
        Record = new() { Data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logline)) }
      });
    }

    static DNS.Protocol.ResourceRecords.ResourceRecord MakeBlockedAsnIpRecord(DNS.Protocol.RecordType type, DNS.Protocol.Domain name) =>
      type switch
      {
        DNS.Protocol.RecordType.A or DNS.Protocol.RecordType.ANY => new(name, IPAddress.Any.GetAddressBytes(), type, DNS.Protocol.RecordClass.IN, FILTERED_RECORD_TTL),
        DNS.Protocol.RecordType.AAAA => new(name, IPAddress.IPv6None.GetAddressBytes(), type, DNS.Protocol.RecordClass.IN, FILTERED_RECORD_TTL),
        _ => throw new ArgumentException($"Unsupported record type {type} for block record")
      };

    static DNS.Protocol.ResourceRecords.ResourceRecord? MakeRedirectRecord(DNS.Protocol.RecordType type, DNS.Protocol.Domain name, IPAddress to) =>
      (type, to.AddressFamily) switch
      {
        (DNS.Protocol.RecordType.A, AddressFamily.InterNetwork) => new(name, to.GetAddressBytes(), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN, FILTERED_RECORD_TTL),
        (DNS.Protocol.RecordType.AAAA, AddressFamily.InterNetworkV6) => new(name, to.GetAddressBytes(), DNS.Protocol.RecordType.AAAA, DNS.Protocol.RecordClass.IN, FILTERED_RECORD_TTL),
        /// MAYBE: double-check that the TO address hasn't been blocked by ASN filtering?
        _ => null
      };

    static async Task<DNS.Protocol.IResponse?> ProxyUpstreamAsync(DNS.Protocol.Question question)
    {
      try
      {
        return await _upstreamClient.Resolve(question.Name.ToString(), question.Type);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Error resolving {question.Name}: {ex.Message}");
        return null;
      }
    }

    async Task<(bool blocked, IPAddress? to)> GetDomainInfo(string domain)
    {
      var now = DateTime.UtcNow;
      var parts = domain.Split('.');
      var roots = parts.Select((_, i) => string.Join('.', parts.Skip(i))).ToArray();

      // Check if all cached and valid
      if (roots.All(r => _cache.TryGetValue(r, out var cached) && now < cached.expires))
      {
        var blocked = false;
        IPAddress? to = null;
        foreach (var root in roots)
        {
          var cached = _cache[root];
          blocked |= cached.blocked;
          to ??= cached.to;
        }
        return (blocked, to);
      }

      return await RefreshDomainCache(roots);
    }

    async Task<(bool blocked, IPAddress? to)> RefreshDomainCache(string[] roots)
    {
      var batchRequest = new BatchGetItemRequest
      {
        RequestItems = new()
        {
          [TABLE_NAME] = new()
          {
            Keys = [
              ..roots.Select(r => new Dictionary<string, AttributeValue>
                {
                    { "PK", new() { S = r } },
                    { "SK", new() { S = r } }
                })
              ]
          }
        }
      };

      var response = await ddb.BatchGetItemAsync(batchRequest);
      var items = response.Responses.GetValueOrDefault(TABLE_NAME, []);
      var itemLookup = items.ToDictionary(item => item["PK"].S);

      var blocked = false;
      IPAddress? to = null;
      var expiration = DateTime.UtcNow.Add(FILTERED_RECORD_TTL);

      foreach (var root in roots)
      {
        var hasItem = itemLookup.TryGetValue(root, out var item);
        var isBlocked = hasItem && item?.TryGetValue("blocked", out var b) == true && b.BOOL == true;
        var redirect = hasItem && item?.TryGetValue("redirect", out var r) == true ? IPAddress.Parse(r.S) : null;

        _cache[root] = (isBlocked, redirect, expiration);
        blocked |= isBlocked;
        to ??= redirect;
      }

      return (blocked, to);
    }

    async Task<(bool blocked, IPAddress? to)> GetAsnInfo(IPAddress ip)
    {
      var now = DateTime.UtcNow;
      var asn = _asnLookup.LookupAsn(ip);
      if (asn is null) return (false, null);

      var (good, blocked, to) = _cache.TryGetValue($"AS#{asn}", out var v) && now < v.expires ?
        (true, v.blocked, v.to) : (false, false, null);

      return good ? (blocked, to) : await RefreshAsnCache(asn.Value);
    }

    async Task<(bool blocked, IPAddress? to)> RefreshAsnCache(uint asn)
    {
      var request = new GetItemRequest
      {
        TableName = TABLE_NAME,
        Key = new()
        {
          { "PK", new() { S = $"AS#{asn}" } },
          { "SK", new() { S = $"AS#{asn}" } }
        }
      };
      var response = await ddb.GetItemAsync(request);
      var item = response.Item;

      var blocked = item?.TryGetValue("blocked", out var b) == true && b.BOOL == true;
      var redirect = item?.TryGetValue("redirect", out var t) == true ? t.S switch { string s => IPAddress.Parse(s), _ => null } : null;

      _cache[$"AS#{asn}"] = (blocked, redirect, DateTime.UtcNow.Add(FILTERED_RECORD_TTL));
      return (blocked, redirect);
    }
  }
}