using System.Collections.Concurrent;
using System.Net;
using Amazon.DynamoDBv2;

namespace DnsFilterLambda;
public class AsnLookupService(IAmazonDynamoDB ddb)
{
    private AsnBlockLookup _asnBlockLookup = null!;

    private ConcurrentDictionary<IPAddress, uint?> _asnCache = [];

    private volatile bool _isInitialized;

    public AsnLookupService() : this (new AmazonDynamoDBClient())
    {
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await RefreshAsync();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var newBlockData = await LoadBlockDataAsync(cancellationToken);
        var newAsnBlockLookup = new AsnBlockLookup(newBlockData);
        var newAsnCache = new ConcurrentDictionary<IPAddress, uint?>(_asnCache.Where(r => newAsnBlockLookup.Lookup(r.Key) == r.Value));

        // Atomically replace the ranges list
        (_asnBlockLookup, _asnCache) = (newAsnBlockLookup, newAsnCache);
        _isInitialized = true;
    }

    private async Task<ReadOnlyMemory<byte>> LoadBlockDataAsync(CancellationToken cancellationToken = default)
    {
        var response = await ddb.GetItemAsync(new()
        {
            TableName = Function.TABLE_NAME,
            Key = new()
            {
                ["PK"] = new("ASN_BLOCK_DATA"),
                ["SK"] = new("ASN_BLOCK_DATA")

            }
        }, cancellationToken);

        if (response.Item?.TryGetValue("Data", out var dataAttr) == true  && dataAttr?.B is MemoryStream dataBytes)
        { 
            return dataBytes.ToArray();
        }

        await Console.Out.WriteLineAsync("No ASN block data found in DynamoDB.");
        return ReadOnlyMemory<byte>.Empty; // Return empty if no data found
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
