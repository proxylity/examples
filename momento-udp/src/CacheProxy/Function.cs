using System.Text;
using Amazon.Lambda.Core;
using Momento.Sdk;
using Momento.Sdk.Auth;
using Momento.Sdk.Config;
using Momento.Sdk.Responses;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

// ---------------------------------------------------------------------------
//  Momento Cache proxy — handles SET, DEL, GET batches from Proxylity
//
//  WIRE PROTOCOL  (UTF-8, newline-delimited)
//  =========================================
//  reqId is the first field in both directions for consistency.
//
//  Requests (client -> Proxylity -> Lambda):
//    GET  "{reqId}\nGET\n{key}"
//    SET  "{reqId}\nSET\n{key}\n{ttlSeconds}\n{base64value}"
//    DEL  "{reqId}\nDEL\n{key}"
//
//  Responses (Lambda -> Proxylity -> client):
//    hit   "{reqId}\nHIT\n{base64value}"
//    miss  "{reqId}\nMISS"
//    ok    "{reqId}\nOK"
//    error "{reqId}\nERR\n{message}"
//
//  Note: {base64value} in SET/HIT is our protocol-level encoding of the
//  cache value bytes. It is unrelated to the Proxylity Formatter setting.
//
//  FORMATTER: utf8
//  ===============
//  The Destination in the SAM template specifies Formatter: utf8, which
//  means Proxylity delivers RequestPacket.Data as a plain UTF-8 string
//  (not base64-encoded). Likewise, ResponsePacket.Data must be a plain
//  UTF-8 string. This eliminates all base64 encode/decode of the packet
//  envelope in this function -- msg.Data is read directly as text, and
//  reply Data is assigned as text.
//
//  SINGLE LISTENER
//  ===============
//  All three operations (GET, SET, DEL) share one listener and one
//  destination. The op code in the packet payload routes internally.
//  A single BatchCount governs all operations; if independent tuning
//  becomes necessary in future, listeners can be split then.
//
//  CONCURRENCY
//  ===========
//  Task.WhenAll(messages.Select(DispatchAsync)) starts all N tasks before
//  awaiting any, so all N Momento gRPC calls are in flight simultaneously.
//  The module-level CacheClient holds a persistent gRPC channel that
//  survives across warm invocations.
// ---------------------------------------------------------------------------

var momentoClient = new CacheClient(
    Configurations.InRegion.Default.Latest(),
    new EnvMomentoV2TokenProvider("MOMENTO_API_KEY"),
    defaultTtl: TimeSpan.FromSeconds(60));

var cacheName = Env("MOMENTO_CACHE_NAME");

await Amazon.Lambda.RuntimeSupport.LambdaBootstrapBuilder
    .Create<ProxylityRequest, ProxylityResponse>(Handler, new Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer())
    .Build()
    .RunAsync();

// ---------------------------------------------------------------------------
//  Handler
// ---------------------------------------------------------------------------

async Task<ProxylityResponse> Handler(ProxylityRequest request, ILambdaContext _)
{
    if (request.Messages is not { Length: > 0 })
        return new ProxylityResponse([]);

    var replies = await Task.WhenAll(request.Messages.Select(DispatchAsync));
    return new ProxylityResponse(replies);
}

// ---------------------------------------------------------------------------
//  Dispatch
//
//  With Formatter: utf8, msg.Data is already a plain UTF-8 string.
//  Protocol: "{reqId}\n{op}\n{key}[\n{ttl}\n{b64value}]"
// ---------------------------------------------------------------------------

async Task<ResponsePacket> DispatchAsync(RequestPacket msg)
{
    var parts = msg.Data.Split('\n', 5);
    if (parts.Length < 3)
        return Err(msg.Tag, "0", $"expected >= 3 fields, got {parts.Length}");

    var (reqId, op, key) = (parts[0], parts[1], parts[2]);

    return op switch
    {
        "GET" => await DoGetAsync(msg.Tag, reqId, key),
        "SET" => parts.Length >= 5
                     ? await DoSetAsync(msg.Tag, reqId, key, parts[3], parts[4])
                     : Err(msg.Tag, reqId, "SET needs 5 fields"),
        "DEL" => await DoDelAsync(msg.Tag, reqId, key),
        _     => Err(msg.Tag, reqId, $"unknown op: {op}")
    };
}

// ---------------------------------------------------------------------------
//  Momento operations
// ---------------------------------------------------------------------------

async Task<ResponsePacket> DoGetAsync(string tag, string reqId, string key)
{
    var r = await momentoClient.GetAsync(cacheName, key);
    return r switch
    {
        CacheGetResponse.Hit hit   => Reply(tag, $"{reqId}\nHIT\n{Convert.ToBase64String(hit.ValueByteArray)}"),
        CacheGetResponse.Miss      => Reply(tag, $"{reqId}\nMISS"),
        CacheGetResponse.Error err => Err(tag, reqId, err.Message),
        _                          => Err(tag, reqId, "unexpected response")
    };
}

async Task<ResponsePacket> DoSetAsync(string tag, string reqId, string key, string ttlStr, string b64Value)
{
    if (!uint.TryParse(ttlStr, out var ttl))
        return Err(tag, reqId, $"invalid ttl: {ttlStr}");

    byte[] value;
    try   { value = Convert.FromBase64String(b64Value); }
    catch { return Err(tag, reqId, "SET value is not valid base64"); }

    var r = await momentoClient.SetAsync(cacheName, key, value, TimeSpan.FromSeconds(ttl));
    return r switch
    {
        CacheSetResponse.Success   => Reply(tag, $"{reqId}\nOK"),
        CacheSetResponse.Error err => Err(tag, reqId, err.Message),
        _                          => Err(tag, reqId, "unexpected response")
    };
}

async Task<ResponsePacket> DoDelAsync(string tag, string reqId, string key)
{
    var r = await momentoClient.DeleteAsync(cacheName, key);
    return r switch
    {
        CacheDeleteResponse.Success   => Reply(tag, $"{reqId}\nOK"),
        CacheDeleteResponse.Error err => Err(tag, reqId, err.Message),
        _                             => Err(tag, reqId, "unexpected response")
    };
}

// ---------------------------------------------------------------------------
//  Helpers
//
//  With Formatter: utf8 the Data field is plain text in both directions.
// ---------------------------------------------------------------------------

static ResponsePacket Reply(string tag, string text) => new(tag, text);
static ResponsePacket Err(string tag, string reqId, string msg) => Reply(tag, $"{reqId}\nERR\n{msg}");

static string Env(string name)
    => Environment.GetEnvironmentVariable(name)
       ?? throw new InvalidOperationException($"Missing env var: {name}");

// ---------------------------------------------------------------------------
//  Proxylity wire types
// ---------------------------------------------------------------------------

public record RequestPacket(string Tag, string Data);
public record ResponsePacket(string Tag, string Data);
public record ProxylityRequest(RequestPacket[] Messages);
public record ProxylityResponse(ResponsePacket[] Replies);
