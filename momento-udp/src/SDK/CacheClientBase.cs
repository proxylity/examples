using System.Collections.Concurrent;
using System.Text;

namespace MomentoUdpCache.Sdk;

// ---------------------------------------------------------------------------
//  Response types — discriminated unions matching the Momento SDK shape.
//
//  The UDP SDK deliberately omits the cacheName parameter that the gRPC SDK
//  carries on each method. The target cache is a server-side policy decision
//  fixed at deploy time via the MOMENTO_CACHE_NAME Lambda environment
//  variable. Keeping it there prevents a client from targeting an arbitrary
//  cache using the stack's Momento API key. Deploy separate stacks if
//  multiple caches are required.
// ---------------------------------------------------------------------------

public abstract class CacheGetResponse
{
    public sealed class Hit : CacheGetResponse
    {
        internal Hit(byte[] value) => ValueByteArray = value;
        public byte[]  ValueByteArray { get; }
        public string  ValueString    => Encoding.UTF8.GetString(ValueByteArray);
    }
    public sealed class Miss  : CacheGetResponse { }
    public sealed class Error : CacheGetResponse
    {
        internal Error(string message) => Message = message;
        public string Message { get; }
    }
}

public abstract class CacheSetResponse
{
    public sealed class Success : CacheSetResponse { }
    public sealed class Error   : CacheSetResponse
    {
        internal Error(string message) => Message = message;
        public string Message { get; }
    }
}

public abstract class CacheDeleteResponse
{
    public sealed class Success : CacheDeleteResponse { }
    public sealed class Error   : CacheDeleteResponse
    {
        internal Error(string message) => Message = message;
        public string Message { get; }
    }
}

// ---------------------------------------------------------------------------
//  Wire protocol (UTF-8, newline-delimited)
//
//  reqId is the first field in both directions.
//
//  Requests:   "{reqId}\nGET\n{key}"
//              "{reqId}\nSET\n{key}\n{ttlSeconds}\n{base64value}"
//              "{reqId}\nDEL\n{key}"
//
//  Responses:  "{reqId}\nHIT\n{base64value}"
//              "{reqId}\nMISS"
//              "{reqId}\nOK"
//              "{reqId}\nERR\n{message}"
//
//  {base64value} is our encoding of binary cache values within the text
//  protocol. The Proxylity Formatter: utf8 setting means the packet payload
//  itself is not additionally base64-wrapped by Proxylity.
//
//  CONCURRENCY
//  ===========
//  A single socket handles all operations. Each request gets a unique reqId
//  from Interlocked.Increment; the receive loop matches incoming responses
//  to pending TaskCompletionSources via ConcurrentDictionary lookup.
//  There is no semaphore, no socket pool -- unlimited concurrent requests.
// ---------------------------------------------------------------------------

public abstract class CacheClientBase : IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<string>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _receiveLoop = Task.CompletedTask;
    private uint _nextReqId;

    private readonly TimeSpan _timeout     = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _defaultTtl;

    protected CacheClientBase(TimeSpan defaultTtl = default)
    {
        _defaultTtl = defaultTtl > TimeSpan.Zero ? defaultTtl : TimeSpan.FromSeconds(300);
    }

    // Call at the end of the derived constructor, after the transport is ready.
    protected void StartReceiveLoop() => _receiveLoop = ReceiveLoopAsync(_cts.Token);

    // -----------------------------------------------------------------------
    //  GET
    // -----------------------------------------------------------------------

    public async Task<CacheGetResponse> GetAsync(string key, CancellationToken ct = default)
    {
        var resp   = await SendAndAwaitAsync($"{{0}}\nGET\n{key}", ct);
        var nl     = resp.IndexOf('\n');
        var status = nl < 0 ? resp : resp[..nl];
        var body   = nl < 0 ? ""   : resp[(nl + 1)..];
        return status switch
        {
            "HIT"  => new CacheGetResponse.Hit(TryFromBase64(body) ?? Array.Empty<byte>()),
            "MISS" => new CacheGetResponse.Miss(),
            "ERR"  => new CacheGetResponse.Error(body),
            _      => new CacheGetResponse.Error($"unexpected: {resp}")
        };
    }

    // -----------------------------------------------------------------------
    //  SET
    // -----------------------------------------------------------------------

    public Task<CacheSetResponse> SetAsync(string key, string value,
        TimeSpan? ttl = null, CancellationToken ct = default)
        => SetAsync(key, Encoding.UTF8.GetBytes(value), ttl, ct);

    public async Task<CacheSetResponse> SetAsync(string key, byte[] value,
        TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var ttlSeconds = (uint)Math.Ceiling((ttl ?? _defaultTtl).TotalSeconds);
        var resp = await SendAndAwaitAsync($"{{0}}\nSET\n{key}\n{ttlSeconds}\n{Convert.ToBase64String(value)}", ct);
        return resp == "OK"
            ? new CacheSetResponse.Success()
            : new CacheSetResponse.Error(resp.StartsWith("ERR\n") ? resp[4..] : resp);
    }

    // -----------------------------------------------------------------------
    //  DELETE
    // -----------------------------------------------------------------------

    public async Task<CacheDeleteResponse> DeleteAsync(string key, CancellationToken ct = default)
    {
        var resp = await SendAndAwaitAsync($"{{0}}\nDEL\n{key}", ct);
        return resp == "OK"
            ? new CacheDeleteResponse.Success()
            : new CacheDeleteResponse.Error(resp.StartsWith("ERR\n") ? resp[4..] : resp);
    }

    // -----------------------------------------------------------------------
    //  Transport abstraction -- implemented by UdpCacheClient / WgCacheClient
    // -----------------------------------------------------------------------

    protected abstract Task SendPacketAsync(byte[] packet, CancellationToken ct);
    protected abstract Task<ReadOnlyMemory<byte>> ReceivePacketAsync(CancellationToken ct);
    protected abstract void DisposeTransport();

    // -----------------------------------------------------------------------
    //  Core: assign reqId, send packet, await TCS completion
    // -----------------------------------------------------------------------

    private async Task<string> SendAndAwaitAsync(string template, CancellationToken ct)
    {
        var id  = Interlocked.Increment(ref _nextReqId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        if (ct.CanBeCanceled)
            ct.Register(() => { if (_pending.TryRemove(id, out _)) tcs.TrySetCanceled(ct); });

        var packet = Encoding.UTF8.GetBytes(string.Format(template, id));
        await SendPacketAsync(packet, ct);

        return await tcs.Task.WaitAsync(_timeout, ct);
    }

    // -----------------------------------------------------------------------
    //  Receive loop
    //
    //  Response format: "{reqId}\n{status}[\n{body}]"
    //  Split on first \n to get reqId, deliver remainder to the awaiter.
    // -----------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> data;
            try   { data = await ReceivePacketAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            try
            {
                var text = Encoding.UTF8.GetString(data.Span).Trim();
                var nl   = text.IndexOf('\n');
                if (nl < 0 || !uint.TryParse(text[..nl], out var id)) continue;
                if (!_pending.TryRemove(id, out var tcs)) continue;
                tcs.TrySetResult(text[(nl + 1)..]);
            }
            catch { /* swallow malformed packets */ }
        }
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static byte[]? TryFromBase64(string s)
    {
        try   { return Convert.FromBase64String(s); }
        catch { return Encoding.UTF8.GetBytes(s); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _receiveLoop; } catch (OperationCanceledException) { }
        DisposeTransport();
        _cts.Dispose();
    }
}
