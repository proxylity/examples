using System.IO.Compression;
using System.Net;
using System.Numerics;

namespace DnsFilterLambda
{
    public class AsnLookupService
    {
        private readonly record struct AsnRange(BigInteger Start, BigInteger End, uint Asn);
        private volatile List<AsnRange> _ranges = [];
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
        private volatile bool _isInitialized;

        public AsnLookupService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;
            await RefreshAsync();
        }

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            await _refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                var newRanges = await LoadRangesAsync(cancellationToken);

                // Atomically replace the ranges list
                _ranges = newRanges;
                _isInitialized = true;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
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
                    return range.Asn;

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
            _refreshSemaphore?.Dispose();
            _httpClient?.Dispose();
        }
    }
}