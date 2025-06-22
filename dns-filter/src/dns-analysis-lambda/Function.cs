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

        StatefulDnsAnalyzer _analyzer = null!;
        StatefulDnsAnalyzer Analyzer => _analyzer ??= new StatefulDnsAnalyzer(Store);

        public Function() : this(new AmazonDynamoDBClient(), new AmazonS3Client(), new AmazonEventBridgeClient()) { }

        // handle eventbridge event that indicates a new DNS log file is available in S3
        public async Task FunctionHandler(JsonObject input, ILambdaContext context)
        {
            using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromMilliseconds(200));

            await Console.Out.WriteLineAsync($"Received event: {input.ToJsonString()}");

            var bucket = input["detail"]?["bucket"]?.ToString() ?? throw new InvalidOperationException("Invalid payload (not from EventBridge?)");
            var key = input["detail"]?["object"]?["key"]?.ToString() ?? throw new InvalidOperationException("Invalid payload (not from EventBridge?)");

            var stream = await s3.GetObjectStreamAsync(bucket, key, null, cts.Token);
            var deduplicated = DeduplicateDnsLogsByDomain(stream, cts.Token);

            var baseDomainsToDomains = deduplicated
                .Select(kv => (parts: DomainUtils.GetDomainParts(kv.Key), queries: kv.Value))
                .ToLookup(a => a.parts.domain ?? string.Empty, a => (a.parts.subdomain, a.queries))
                .ToDictionary(g => g.Key, g => g.ToDictionary(b => b.subdomain ?? string.Empty, b => b.queries));

            var domainData = await _store.BatchGetAsync(baseDomainsToDomains.Keys, cts.Token);
            var domainDataMap = domainData.Where(d => d is not null).ToDictionary(d => d!.Domain);

            var updatedDomainData = baseDomainsToDomains.Select(kv =>
            {
                var (domain, subdomains) = kv;
                domainDataMap.TryGetValue(domain, out var state);
                state ??= new()
                {
                    Domain = domain
                };

                foreach (var (subdomain, queries) in subdomains)
                {
                    foreach (var (qtype, rcode, timestamp) in queries)
                    {
                        state.TotalQueries++;
                        if (rcode == "NameError")
                            state.NxDomainCount++;
                    }
                    state.UniqueSubdomains.Add(subdomain ?? domain);
                }

                state.Version++;
                state.Expires = DateTime.UtcNow.AddDays(7); // Reset expiry

                return state;
            }).ToList();

            var notify = NotifyOfNewlySuspiciousDomains(updatedDomainData, cts.Token);
            var updateTasks = updatedDomainData.Select(state => _store.UpdateAsync(state, cts.Token));
            await Task.WhenAll([notify, ..updateTasks]);
        }

        private async Task NotifyOfNewlySuspiciousDomains(List<DomainState> updatedDomainData, CancellationToken cancellationToken = default)
        {
            ICollection<string> reasons = [];
            var newlySuspiciousDomains = updatedDomainData
                .Where(d => !d.IsSuspicious && (StatelessDnsAnalyzer.IsSuspicious(d, out reasons)
                    || _analyzer.IsSuspicious(d, out reasons)));
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
                            Detail = System.Text.Json.JsonSerializer.Serialize(new { domain.Domain, domain.TotalQueries, domain.NxDomainCount, Reasons = reasons }),
                            Source = "dns-filter",
                            EventBusName = EVENT_BUS_NAME,
                        }
                    ]
                }, cancellationToken);
            }
        }

        private static ConcurrentDictionary<string, HashSet<(string qtype, string rcode, DateTime timestamp)>> DeduplicateDnsLogsByDomain(Stream stream, CancellationToken token)
        {
            // log line example: 2025-06-16T20:43:32.8209308Z example.com\tA\tNoError\tA 600780C6,A 17C0E454,A 17D70088,A 600780AF,A 17D7008A,A 17C0E450
            using var decompress = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(decompress);

            var seen = new ConcurrentDictionary<string, HashSet<(string qtype, string rcode, DateTime timestamp)>>();
            while (reader.ReadLine() is string line)
            {
                token.ThrowIfCancellationRequested();

                var parts = line.Split('\t', 5);

                if (parts.Length != 5) continue;
                if (!DateTime.TryParse(parts[0], out var timestamp)) continue;

                var fqdn = parts[1];
                var qtype = parts[2];
                var rcode = parts[3];
                var answers = parts[4];
                seen.AddOrUpdate(fqdn, _ => [ (qtype, rcode, timestamp) ], (_, set) => { set.Add((qtype, rcode, timestamp)); return set; });
            }
            return seen;
        }
    }
}