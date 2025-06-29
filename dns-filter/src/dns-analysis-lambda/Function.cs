using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2.Model;
using DnsFilterLambda;
using Amazon.S3;
using System.Collections.Concurrent;
using Amazon.EventBridge;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace DnsAnalysisLambda
{
    public class Function(IAmazonDynamoDB ddb, IAmazonS3 s3, IAmazonEventBridge eb)
    {
        static readonly string TABLE_NAME = Environment.GetEnvironmentVariable("TABLE_NAME") ?? throw new InvalidOperationException($"{nameof(TABLE_NAME)} environment variable is not set");
        static readonly string EVENT_BUS_NAME = Environment.GetEnvironmentVariable("EVENT_BUS_NAME") ?? "default";

        DomainStateStore _store = null!;
        DomainStateStore Store => _store ??= new DomainStateStore(ddb, TABLE_NAME);

        public Function() : this(new AmazonDynamoDBClient(), new AmazonS3Client(), new AmazonEventBridgeClient()) { }

        // handle eventbridge event that indicates a new DNS log file is available in S3
        public async Task FunctionHandler(JsonObject input, ILambdaContext context)
        {
            using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromMilliseconds(200));

            await Console.Out.WriteLineAsync($"Received event: {input.ToJsonString()}");

            var bucket = input["detail"]?["bucket"]?["name"]?.ToString() ?? throw new InvalidOperationException("Invalid payload (not from EventBridge?)");
            var key = input["detail"]?["object"]?["key"]?.ToString() ?? throw new InvalidOperationException("Invalid payload (not from EventBridge?)");

            var stream = await s3.GetObjectStreamAsync(bucket, key, null, cts.Token);
            var grouped = GroupDnsLogsByDomain(stream, cts.Token);

            var baseDomainsToSubdomains = grouped
                .Select(kv => (parts: DomainUtils.GetDomainParts(kv.Key), queries: kv.Value))
                .ToLookup(a => a.parts.baseDomain ?? string.Empty, a => (a.parts.subdomain, a.queries))
                .ToDictionary(g => g.Key, g => g.ToDictionary(b => b.subdomain ?? string.Empty, b => b.queries));

            await Console.Out.WriteLineAsync($"Base domains to process: \n{string.Join("\n", baseDomainsToSubdomains.Keys)}");

            var attempts = 0;
            var delay = 200;
            while (attempts < 3)
            {
                try
                {
                    var baseDomainData = await Store.BatchGetAsync(baseDomainsToSubdomains.Keys, true, cts.Token);
                    var baseDomainDataMap = baseDomainData.ToDictionary(d => d!.Domain);

                    var updatedBaseDomainData = baseDomainsToSubdomains.Select(kv =>
                    {
                        var (domain, queries) = kv;
                        baseDomainDataMap.TryGetValue(domain, out var state);
                        state ??= new() { Domain = domain };

                        var nxcount = queries.Sum(s => s.Value.Count(c => c.rcode == "NameError"));
                        DnsQueryAnalyzer.UpdateDomainStateStatistics(state!, queries.Keys, nxcount);

                        state.Expires = DateTime.UtcNow.AddDays(7); // Reset expiry

                        return state;
                    }).ToList();

                    await NotifyOfNewlySuspiciousDomains(updatedBaseDomainData, cts.Token); // mark newly suspicious domains and notify
                    await Store.BatchUpdateAsync(updatedBaseDomainData, cts.Token);

                    return; // exit on success
                }
                catch (Exception e)
                {
                    attempts++;
                    await Console.Error.WriteLineAsync($"Attempt {attempts} failed: {e.Message}. Retrying...");
                    await Task.Delay(TimeSpan.FromMilliseconds(delay + Random.Shared.Next(0, delay)), cts.Token); // wait before retrying
                    delay *= 2;
                }
            }

            await Console.Error.WriteLineAsync($"Failed to process DNS logs after {attempts}.");
            throw new Exception("Failed to process DNS log batch.");
        }

        private async Task NotifyOfNewlySuspiciousDomains(List<DomainState> updatedBaseDomainData, CancellationToken cancellationToken = default)
        {
            ICollection<string> reasons = [];
            var newlySuspiciousDomains = updatedBaseDomainData
                .Where(d => !d.IsSuspicious && DnsQueryAnalyzer.IsSuspicious(d, out reasons));
            foreach (var domain in newlySuspiciousDomains)
            {
                domain.IsSuspicious = true;
                await Console.Out.WriteLineAsync($"Domain {domain.Domain} is newly suspicious: {string.Join(", ", reasons)}");
                // fire eventbridge event for newly suspicious domain
                await eb.PutEventsAsync(new()
                {
                    Entries =
                    [
                        new()
                        {
                            DetailType = "suspicious-domain",
                            Detail = System.Text.Json.JsonSerializer.Serialize(new {
                                domain.Domain,
                                domain.TotalQueries,
                                domain.NxDomainCount,
                                Reasons = reasons
                            }),
                            Source = "dns-filter",
                            EventBusName = EVENT_BUS_NAME,
                        }
                    ]
                }, cancellationToken);
            }
        }

        private static Dictionary<string, HashSet<(string qtype, string rcode, DateTime timestamp)>> GroupDnsLogsByDomain(Stream stream, CancellationToken token)
        {
            // log line example: 2025-06-16T20:43:32.8209308Z example.com\tA\tNoError\tA 600780C6,A 17C0E454,A 17D70088,A 600780AF,A 17D7008A,A 17C0E450
            using var decompress = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(decompress);

            var seen = new Dictionary<string, HashSet<(string qtype, string rcode, DateTime timestamp)>>();
            while (reader.ReadLine() is string line)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split('\t', 5);

                if (parts.Length != 5) continue;
                if (!DateTime.TryParse(parts[0], out var timestamp)) continue;

                var fqdn = parts[1]; // TODO: is missing somtimes, perhaps bogus requests?                
                var qtype = parts[2];
                var rcode = parts[3];
                var answers = parts[4];

                var set = seen.TryGetValue(fqdn, out var s) ? s : [];
                set.Add((qtype, rcode, timestamp));
                seen[fqdn] = set;
            }
            return seen;
        }
    }
}