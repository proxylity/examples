using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using System.Text.Json.Nodes;
using System.Net;
using Amazon.DynamoDBv2.Model;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DnsFilterLambda
{
  public class Function(IAmazonDynamoDB ddb)
  {
    static readonly string TABLE_NAME = Environment.GetEnvironmentVariable("TABLE_NAME") ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set");
    static readonly IPAddress BLOCK_IP = IPAddress.Parse(Environment.GetEnvironmentVariable("BLOCK_IP") ?? "0.0.0.0");
    static readonly IPAddress UPSTREAM_DNS = IPAddress.Parse(Environment.GetEnvironmentVariable("UPSTREAM_DNS") ?? "1.0.0.3");
    static readonly TimeSpan FILTERED_RECORD_TTL = TimeSpan.FromSeconds(300);

    readonly Dictionary<string, (bool blocked, IPAddress? to, DateTime expires)> _cache = [];
    readonly AsnLookupService _asnLookup = new();
    static readonly DNS.Client.DnsClient _upstreamClient = new(UPSTREAM_DNS);

    public Function() : this(new AmazonDynamoDBClient()) { }

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
      var request = DNS.Protocol.Request.FromArray(data);
      var response = DNS.Protocol.Response.FromRequest(request);

      foreach (var question in request.Questions)
      {
        if (question.Type is DNS.Protocol.RecordType.A or DNS.Protocol.RecordType.AAAA)
        {
          var domain = question.Name.ToString().TrimEnd('.');
          var (blocked, to) = await GetDomainInfo(domain);

          if (blocked)
          {
            response.AnswerRecords.Add(MakeARecord(question.Name, BLOCK_IP));
            continue;
          }

          if (to is not null)
          {
            response.AnswerRecords.Add(MakeARecord(question.Name, to));
            continue;
          }
        }

        // Proxy
        var result = await ProxyUpstreamAsync(question);
        foreach (var r in result?.AnswerRecords ?? []) response.AnswerRecords.Add(r);
      }

      // Process answer records for ASN filtering
      var filteredAnswers = new List<DNS.Protocol.ResourceRecords.IResourceRecord>();
      foreach (var answer in response.AnswerRecords)
      {
        if (answer.Type is DNS.Protocol.RecordType.A or DNS.Protocol.RecordType.AAAA)
        {
          var (blocked, to) = await GetAsnInfo(new IPAddress(answer.Data));
          if (blocked)
          {
            // Replace with blocked IP
            filteredAnswers.Add(new DNS.Protocol.ResourceRecords.ResourceRecord(
              answer.Name,
              BLOCK_IP.GetAddressBytes(),
              answer.Type,
              answer.Class,
              FILTERED_RECORD_TTL));
          }
          else if (to is not null)
          {
            // Replace with redirect IP
            filteredAnswers.Add(new DNS.Protocol.ResourceRecords.ResourceRecord(
              answer.Name,
              to.GetAddressBytes(),
              answer.Type,
              answer.Class,
              FILTERED_RECORD_TTL));
          }
          else
          {
            // Keep original answer
            filteredAnswers.Add(answer);
          }
        }
        else
        {
          // Keep non-IP answers as-is
          filteredAnswers.Add(answer);
        }
      }

      // Replace answer records with filtered ones
      response.AnswerRecords.Clear();
      foreach (var answer in filteredAnswers)
      {
        response.AnswerRecords.Add(answer);
      }

      return new()
      {
        ["Tag"] = tag,
        ["Data"] = Convert.ToBase64String(response.ToArray())
      };
    }

    static DNS.Protocol.ResourceRecords.ResourceRecord MakeARecord(DNS.Protocol.Domain name, System.Net.IPAddress ip) =>
      ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
          ? new DNS.Protocol.ResourceRecords.ResourceRecord(name, ip.GetAddressBytes(), DNS.Protocol.RecordType.A, DNS.Protocol.RecordClass.IN, FILTERED_RECORD_TTL)
          : throw new ArgumentException("Only IPv4 addresses supported for A record");

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
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                [TABLE_NAME] = new KeysAndAttributes
                {
                    Keys = [.. roots.Select(r => new Dictionary<string, AttributeValue>
                    {
                        { "PK", new AttributeValue { S = r } },
                        { "SK", new AttributeValue { S = r } }
                    })]
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
            var isBlocked = hasItem && item.TryGetValue("blocked", out var b) && b.BOOL;
            var redirect = hasItem && item.TryGetValue("redirect", out var r) ? IPAddress.Parse(r.S) : null;

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
        (true, v.blocked, v.to)
        : (false, false, (IPAddress?)null);

      return good ? (blocked, to) : await RefreshAsnCache(asn.Value);
    }

    async Task<(bool blocked, IPAddress? to)> RefreshAsnCache(uint asn)
    {
      var request = new GetItemRequest
      {
        TableName = TABLE_NAME,
        Key = new Dictionary<string, AttributeValue>
        {
          { "PK", new AttributeValue { S = $"AS#{asn}" } },
          { "SK", new AttributeValue { S = $"AS#{asn}" } }
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