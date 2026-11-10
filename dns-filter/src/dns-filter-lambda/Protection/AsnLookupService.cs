using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;

namespace DnsFilterLambda;

public class AsnLookupService(IAmazonS3 s3)
{
    private AsnBlockLookup _asnBlockLookup = null!;

    private ConcurrentDictionary<IPAddress, uint?> _asnCache = [];

    private volatile bool _isInitialized;
    private DateTime _nextRefresh;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AsnLookupService() : this (new AmazonS3Client())
    {
    }

    public async Task InitializeAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_isInitialized && DateTime.UtcNow < _nextRefresh) return;
            try
            {
                await RefreshAsync();
                _nextRefresh = DateTime.UtcNow.AddMinutes(5);
            }
            catch (Exception error) when (_isInitialized && error is AmazonS3Exception or IOException or HttpRequestException or TaskCanceledException)
            {
                await Console.Error.WriteLineAsync($"ASN refresh failed; retaining last loaded data: {error.Message}");
                _nextRefresh = DateTime.UtcNow.AddMinutes(1);
            }
        }
        finally { _refreshLock.Release(); }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var newBlockData = await LoadAsnLookupDataAsync(cancellationToken);
        if (newBlockData.Length % 40 != 0) throw new InvalidDataException("Invalid ASN binary record length.");
        var newAsnBlockLookup = new AsnBlockLookup(newBlockData);
        var newAsnCache = new ConcurrentDictionary<IPAddress, uint?>(_asnCache.Where(r => newAsnBlockLookup.Lookup(r.Key) == r.Value));

        // Atomically replace the ranges list
        (_asnBlockLookup, _asnCache) = (newAsnBlockLookup, newAsnCache);
        _isInitialized = true;
    }

    private async Task<ReadOnlyMemory<byte>> LoadAsnLookupDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(new()
            {
                BucketName = Function.BUCKET_NAME,
                Key = "asn-from-ip-data.bin"
            }, cancellationToken);

            using var stream = response.ResponseStream;
            var buffer = new byte[response.ContentLength];
            await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)response.ContentLength), cancellationToken);
            return buffer;
        } catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && !_isInitialized)
        {
            await Console.Error.WriteLineAsync($"Failed to get ASN block data from S3: {ex.Message}");
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    public uint? LookupAsn(IPAddress ipAddress)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync() first.");

        return _asnCache.TryGetValue(ipAddress, out var cachedAsn) ?
            cachedAsn 
            : _asnCache[ipAddress] = _asnBlockLookup.Lookup(ipAddress);
    }
}
