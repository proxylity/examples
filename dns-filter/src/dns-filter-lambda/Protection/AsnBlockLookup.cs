using System.Buffers.Binary;
using System.Net;

namespace DnsFilterLambda;

public class AsnBlockLookup(ReadOnlyMemory<byte> data)
{
    private const int RecordSize = 37;

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

            var startIp = record.Slice(1, 16);
            var endIp = record.Slice(17, 16);

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
                uint asn = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(33, 4));
                return asn;
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
