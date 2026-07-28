using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.EventBridge;
using Amazon.Lambda.Core;
using Amazon.SQS;
using CoapDemo.Common.Data;
using CoapDemo.Common.Protocol;
using CoapDemo.Common.Resources;
using CoapDemo.Common.Scheduling;

namespace RequestHandler;

/// <summary>
/// The single Lambda Destination behind the CoAP Demo's UDP Gateway Listener. Never parses
/// or serializes CoAP packets itself -- UDP Gateway's "coap" formatter has already turned
/// each packet into a <see cref="CoapRequest"/> by the time it reaches <see cref="FunctionHandler"/>,
/// and turns the returned <see cref="CoapResponse"/> back into wire bytes. This function is
/// purely resource routing, content negotiation, and application logic (design.md "Lambda Design").
/// </summary>
public sealed class Function
{
    private readonly ResourceRegistry _registry;
    private readonly ObserveRegistry _observeRegistry;
    private readonly RegionalMetricsStore _regionalMetrics;
    private readonly SystemStateStore _systemState;
    private readonly string _region;
    private static bool s_seeded;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    public Function() : this(new AmazonDynamoDBClient(), new AmazonSQSClient(), new AmazonEventBridgeClient())
    {
    }

    public Function(IAmazonDynamoDB ddb, IAmazonSQS sqs, IAmazonEventBridge eventBridge)
    {
        _region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown";

        var globalTableName = Env("GLOBAL_TABLE_NAME");
        var regionalTableName = Env("REGIONAL_TABLE_NAME");
        var observeTtlSeconds = int.Parse(Environment.GetEnvironmentVariable("OBSERVE_TTL_SECONDS") ?? "120");
        var asyncDelaySeconds = int.Parse(Environment.GetEnvironmentVariable("ASYNC_DELAY_SECONDS") ?? "5");

        _observeRegistry = new ObserveRegistry(ddb, globalTableName);
        _systemState = new SystemStateStore(ddb, globalTableName);
        _regionalMetrics = new RegionalMetricsStore(ddb, regionalTableName);

        var asyncScheduler = new SqsAsyncResponseScheduler(
            sqs,
            queueUrl: Env("ASYNC_QUEUE_URL"));

        _registry = BuildRegistry(asyncScheduler, eventBridge, observeTtlSeconds, TimeSpan.FromSeconds(asyncDelaySeconds));
    }

    private ResourceRegistry BuildRegistry(IAsyncResponseScheduler asyncScheduler, IAmazonEventBridge eventBridge, int observeTtlSeconds, TimeSpan asyncDelay)
    {
        var resources = new List<ICoapResource>
        {
            new StaticTextResource("/", "Proxylity UDP Gateway", InfoText.Home),
            new StaticTextResource("/info/about", "About Proxylity UDP Gateway", InfoText.About),
            new StaticTextResource("/info/pricing", "Pricing", InfoText.Pricing),
            new StaticTextResource("/info/features", "Features", InfoText.Features),
            new StaticTextResource("/info/docs", "Documentation", InfoText.Docs),
            new StaticTextResource("/info/examples", "Example Projects", InfoText.Examples),

            new ContactResource(eventBridge, Env("EVENT_BUS_NAME")),

            new ConfirmabilityDemoResource("/coap/con", "Confirmable Response Demo", CoapTypes.Confirmable),
            new ConfirmabilityDemoResource("/coap/non", "Non-confirmable Response Demo", CoapTypes.NonConfirmable),
            new AsyncResource(asyncScheduler, asyncDelay),
            new BinaryResource(),
            new LargeResource(),
            new EchoResource(),
            new ObserversResource(_observeRegistry, observeTtlSeconds),

            new RequestEchoResource(),
            new RegionResource(_region),
            new PingResource(),
            new UploadResource(_regionalMetrics),

            new HealthResource(_systemState, _observeRegistry, observeTtlSeconds),
            new VersionResource(_systemState, _observeRegistry, observeTtlSeconds),
            new MetricsResource(_systemState, _observeRegistry, observeTtlSeconds),
            new TimeResource(_systemState, _observeRegistry, observeTtlSeconds),
        };

        var discoveryRegistry = new ResourceRegistry(resources);
        resources.Add(new DiscoveryResource(discoveryRegistry));
        return new ResourceRegistry(resources);
    }

    public async Task<ProxylityLambdaResponse> FunctionHandler(ProxylityLambdaRequest request, ILambdaContext context)
    {
        await EnsureSeededAsync();

        if (request.Messages is not { Length: > 0 })
            return new ProxylityLambdaResponse { Replies = [] };

        var replies = new List<ResponsePacket>();
        foreach (var message in request.Messages)
        {
            var reply = await ProcessMessageAsync(message, context);
            if (reply is not null) replies.Add(reply);
        }

        return new ProxylityLambdaResponse { Replies = replies };
    }

    private async Task<ResponsePacket?> ProcessMessageAsync(RequestPacket message, ILambdaContext context)
    {
        CoapRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(message.Data, CoapJsonContext.Default.CoapRequest);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to parse CoAP request: {ex.Message}");
            return null;
        }

        if (request is null) return null;

        // Bookkeeping messages (RFC 7252 §4.2/§4.5) never receive a reply.
        if (request.Type == CoapTypes.Acknowledgement)
            return null;

        if (request.Type == CoapTypes.Reset)
        {
            if (!string.IsNullOrEmpty(request.Token))
                await DeregisterEverywhereAsync(message.Remote.IpAddress, message.Remote.Port, request.Token);
            return null;
        }

        await Task.WhenAll(
            _regionalMetrics.RecordRequestAsync(),
            _regionalMetrics.RecordRequestTimeAsync(DateTimeOffset.UtcNow.ToString("O")));

        var exchange = new CoapExchange
        {
            Request = request,
            ClientAddress = message.Remote.IpAddress,
            ClientPort = message.Remote.Port,
            Region = _region,
        };

        var result = _registry.TryGet(request.Path, out var resource)
            ? await resource.HandleAsync(exchange, CancellationToken.None)
            : NotFound(request);

        if (result.Suppressed) return null;

        // RFC 7252 §4.1: an Empty message (Code 0.00) MUST have a zero-length Token and no
        // Options or Payload -- it must be exactly 4 bytes on the wire. Every other response
        // echoes the request's Token so the client can correlate the reply. The zero-length Token
        // is expressed as "" rather than null, matching this codebase's convention that an empty
        // string (not null) encodes a zero-length value (see CoapOption.Value).
        var isEmpty = result.Code == CoapCodes.Empty;
        var response = new CoapResponse
        {
            Type = result.Type ?? CoapTypes.Acknowledgement,
            Code = result.Code,
            MessageId = request.MessageId,
            Token = isEmpty ? "" : request.Token,
            Options = isEmpty ? null : result.Options,
            Payload = isEmpty ? null : (result.Payload is { Length: > 0 } ? Convert.ToBase64String(result.Payload) : null),
        };

        return new ResponsePacket
        {
            Tag = message.Tag,
            Data = JsonSerializer.Serialize(response, CoapJsonContext.Default.CoapResponse),
        };
    }

    private static CoapResult NotFound(CoapRequest request) => request.Type == CoapTypes.Confirmable
        ? new CoapResult { Code = CoapCodes.NotFound, Payload = Encoding.UTF8.GetBytes("Not Found") }
        : new CoapResult { Code = CoapCodes.NotFound, Suppressed = true };

    private async Task DeregisterEverywhereAsync(string address, int port, string token)
    {
        var observableResources = _registry.All.Where(r => r.Observable);
        await Task.WhenAll(observableResources.Select(r => _observeRegistry.DeregisterAsync(r.Path, address, port, token)));
    }

    /// <summary>Seeds the static health/version items on first invocation in a cold-started environment (design.md: "written at deployment time").</summary>
    private async Task EnsureSeededAsync()
    {
        if (s_seeded) return;
        await SeedLock.WaitAsync();
        try
        {
            if (s_seeded) return;
            var version = Environment.GetEnvironmentVariable("DEPLOYED_VERSION") ?? "dev";
            await Task.WhenAll(
                _systemState.SeedIfMissingAsync(SystemStateStore.HealthPath, "Status", "healthy", _region),
                _systemState.SeedIfMissingAsync(SystemStateStore.VersionPath, "Version", version, _region));
            s_seeded = true;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private static string Env(string name)
        => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
