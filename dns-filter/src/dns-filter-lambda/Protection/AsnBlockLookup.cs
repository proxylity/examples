using System.Buffers.Binary;
using System.Net;

namespace DnsFilterLambda;

public class AsnBlockLookup(ReadOnlyMemory<byte> data)
{
    private const int RecordSize = 40; // need 37, padded to a multiple of 4

    public uint? Lookup(IPAddress ip)
    {
        var ipBytes = ip.GetAddressBytes();
        if (ipBytes.Length == 4)
            ipBytes = PadIpv4ToIpv6(ipBytes);

        int low = 0;
        int high = (data.Length / RecordSize) - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            var record = data.Slice(mid * RecordSize, RecordSize).Span;

            var _ = record[..4]; // reserved, unused
            var startIp = record[4..20];
            var endIp = record[20..36];
            var asn = record[36..40];

            int cmpStart = CompareIp(ipBytes, startIp);
            int cmpEnd = CompareIp(ipBytes, endIp);

            if (cmpStart < 0)
            {
                high = mid - 1;
            }
            else if (cmpEnd > 0)
            {
                low = mid + 1;
            }
            else
            {
                return BinaryPrimitives.ReadUInt32BigEndian(asn);
            }
        }

        return null; // Not found
    }

    private static int CompareIp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < 16; i++)
        {
            int diff = a[i].CompareTo(b[i]);
            if (diff != 0)
                return diff;
        }
        return 0;
    }

    private static byte[] PadIpv4ToIpv6(byte[] ipv4Bytes)
    {
        return [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, ..ipv4Bytes];
    }
}
