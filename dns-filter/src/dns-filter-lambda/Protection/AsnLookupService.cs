using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;

namespace DnsFilterLambda;

public class AsnLookupService(IAmazonS3 s3)
{
    private AsnBlockLookup _asnBlockLookup = null!;

    private ConcurrentDictionary<IPAddress, uint?> _asnCache = [];

    private volatile bool _isInitialized;

    public AsnLookupService() : this (new AmazonS3Client())
    {
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await RefreshAsync();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var newBlockData = await LoadAsnLookupDataAsync(cancellationToken);
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
        } catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"Failed to get ASN block data from S3: {ex.Message}");
            return ReadOnlyMemory<byte>.Empty; // Return empty if not found or error
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
