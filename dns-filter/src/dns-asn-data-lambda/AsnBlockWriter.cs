using System.Buffers.Binary;

namespace DnsAsnDataLambda;

class AsnBlockWriter()
{
    private const int RECORD_SIZE = 40; // need 36, padded by 4

    public static ReadOnlyMemory<byte> ProcessWriteAsnData(IList<AsnRange> ranges, CancellationToken cancellationToken = default)
    {
        var count = ranges.Count;
        var buffer = new byte[count * RECORD_SIZE];
        for (int i = 0; i < count; ++i)
        { 
            var range = ranges[i];
            var offset = RECORD_SIZE * i;

            var startIp = range.Start.ToByteArray();
            var endIp = range.End.ToByteArray();
            var asn = range.Asn;

            // Copy start IP
            Buffer.BlockCopy(startIp, 0, buffer, offset + 4, startIp.Length);
            // Copy end IP
            Buffer.BlockCopy(endIp, 0, buffer, offset + 20, endIp.Length);
            // Copy ASN
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 36), asn);
        }
        return buffer;
    }
}