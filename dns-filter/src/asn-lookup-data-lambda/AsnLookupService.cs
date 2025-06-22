using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Numerics;

namespace DnsFilterLambda;
public class AsnLookupService(HttpClient? httpClient = null)
{
    private readonly record struct AsnRange(BigInteger Start, BigInteger End, uint Asn);
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private volatile List<AsnRange> _ranges = [];

    private readonly ConcurrentDictionary<IPAddress, uint?> _asnCache = [];

    private volatile bool _isInitialized;

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await RefreshAsync();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var newRanges = await LoadRangesAsync(cancellationToken);

        // Atomically replace the ranges list
        _ranges = newRanges;
        _isInitialized = true;
    }

    private async Task<List<AsnRange>> LoadRangesAsync(CancellationToken cancellationToken = default)
    {
        const string url = "https://iptoasn.com/data/ip2asn-combined.tsv.gz";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var ranges = new List<AsnRange>();
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            if (!IPAddress.TryParse(parts[0], out var startIp) ||
                !IPAddress.TryParse(parts[1], out var endIp) ||
                !uint.TryParse(parts[2], out var asn))
                continue;

            var start = IpToBigInteger(startIp);
            var end = IpToBigInteger(endIp);

            ranges.Add(new AsnRange(start, end, asn));
        }

        // Sort ranges by start address for binary search
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        return ranges;
    }

    public uint? LookupAsn(IPAddress ipAddress)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync() first.");

        if (_asnCache.TryGetValue(ipAddress, out var cachedAsn))
        {
            return cachedAsn; // Return cached result if available
        }

        // Get a reference to the current ranges (atomic read due to volatile)
        var currentRanges = _ranges;
        var target = IpToBigInteger(ipAddress);

        // Binary search for the range containing the target IP
        int left = 0, right = currentRanges.Count - 1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            var range = currentRanges[mid];

            if (target >= range.Start && target <= range.End)
            {
                // Cache the result for future lookups
                return _asnCache[ipAddress] = range.Asn;
            }

            if (target < range.Start)
                    right = mid - 1;
                else
                    left = mid + 1;
        }

        return null;
    }

    private static BigInteger IpToBigInteger(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();

        // Ensure big-endian byte order for consistent comparison
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        // Add a zero byte to ensure positive BigInteger for IPv6
        byte[] paddedBytes = [0, .. bytes];

        return new BigInteger(paddedBytes);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
