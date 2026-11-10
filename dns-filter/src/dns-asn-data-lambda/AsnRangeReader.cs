using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using Amazon.DynamoDBv2;

namespace DnsAsnDataLambda;

record struct AsnRange(BigInteger Start, BigInteger End, uint Asn);

class AsnRangeReader()
{
    public static async Task<List<AsnRange>> ReadAsnRangesAsync(Stream stream, HashSet<uint> blockedAsns, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);

        var ranges = new List<AsnRange>();
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) throw new FormatException("Incomplete IP-to-ASN data row.");

            if (!IPAddress.TryParse(parts[0], out var startIp) ||
                !IPAddress.TryParse(parts[1], out var endIp) ||
                !uint.TryParse(parts[2], out var asn))
                throw new FormatException("Invalid IP-to-ASN data row.");

            if (!blockedAsns.Contains(asn))
                continue;

            var start = IpToBigInteger(startIp);
            var end = IpToBigInteger(endIp);

            ranges.Add(new AsnRange(start, end, asn));
        }

        // Sort ranges by start address for binary search
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        return ranges;
    }

    private static BigInteger IpToBigInteger(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ip = ip.MapToIPv6();
        return new BigInteger(ip.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
    }
}
