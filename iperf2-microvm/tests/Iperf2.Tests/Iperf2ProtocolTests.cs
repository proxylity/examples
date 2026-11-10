using System.Buffers.Binary;
using Iperf2.MicroVm;
using Xunit;

namespace Iperf2.Tests;

public sealed class Iperf2ProtocolTests
{
    // Captured from a stock iperf 2.1.9 client talking to a stock server (scripts/research/run-capture.sh):
    // a 1 Mbit/s, 2 second test of 182 datagrams, 267,540 bytes, no loss, 36 microseconds of jitter.
    private const string StockFinHeader = "ffffff4a6af229310002807effffffff48010098000000010000138900000000";
    private const string StockReport =
        "0000000000000000000000000000000088000000000000000004151400000002" +
        "0000432c0000000000000000000000b600000000000000240000000000000068" +
        "000000000000072c0000000000007f1c00000000000000b2002fc6e80001018e" +
        "00000000ffffffd0000000b60000005a00000001000000000000000000000000";

    [Fact]
    public void ParsesStockFinalDatagram()
    {
        var bytes = Convert.FromHexString(StockFinHeader);
        Assert.True(Iperf2Protocol.TryParse(bytes, out var datagram));
        Assert.Equal(-182L, datagram.Sequence);
        Assert.Equal(182L, datagram.AbsoluteSequence);
        Assert.True(datagram.IsFinal);
        Assert.True(datagram.Uses64BitSequence);
    }

    [Fact]
    public void ParsesStockFirstDataDatagram()
    {
        // The first datagram carries the sequence number 1 (they start at 1, not 0) and the client flags.
        var bytes = Convert.FromHexString("000000016af2292f000236a900000000480100980000000100001389");
        Assert.True(Iperf2Protocol.TryParse(bytes, out var datagram));
        Assert.Equal(1L, datagram.Sequence);
        Assert.False(datagram.IsFinal);
        Assert.True(datagram.Uses64BitSequence);
        Assert.Equal(0x6af2292fL * 1_000_000 + 0x236a9, datagram.SentMicroseconds);
    }

    [Fact]
    public void ReportMatchesStockServerAtEveryFieldWeFill()
    {
        var ours = Iperf2Protocol.WriteServerReport(true,
            new(ReceivedBytes: 267_540, DurationMicroseconds: 2_017_196, LostDatagrams: 0,
                OutOfOrderDatagrams: 0, Datagrams: 182, JitterMicroseconds: 36));
        var stock = Convert.FromHexString(StockReport);

        Assert.Equal(stock.Length, ours.Length);
        // Bytes 0-15 (datagram header) are zero in both; 16-55 hold flags, bytes, time, loss, ooo, datagrams, jitter.
        // Bytes 56+ hold extra statistics (histogram-style fields) that the client does not need for its summary.
        Assert.Equal(stock[..56], ours[..56]);
        // Packets per second and the trailing version word.
        Assert.Equal(stock[108..116], ours[108..116]);
    }

    [Fact]
    public void LegacyReportIsShorterAndKeepsReferenceOffsets()
    {
        var response = Iperf2Protocol.WriteServerReport(false,
            new(0x00000002_00000003, 10_250_000, 4, 2, 100, 12_345));

        Assert.Equal(112, response.Length);
        Assert.Equal(0x80000000u, ReadUInt32(response, 12));
        Assert.Equal(2u, ReadUInt32(response, 16));
        Assert.Equal(3u, ReadUInt32(response, 20));
        Assert.Equal(10, ReadInt32(response, 24));
        Assert.Equal(250_000, ReadInt32(response, 28));
        Assert.Equal(4, ReadInt32(response, 32));
        Assert.Equal(2, ReadInt32(response, 36));
        Assert.Equal(100, ReadInt32(response, 40));
        Assert.Equal(12_345, ReadInt32(response, 48));
        Assert.Equal(9, ReadInt32(response, 104));
        Assert.Equal(1, ReadInt32(response, 108));
    }

    [Fact]
    public void RejectsTruncatedAndInvalidTimestampHeaders()
    {
        Assert.False(Iperf2Protocol.TryParse(new byte[11], out _));
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 1_000_000);
        Assert.False(Iperf2Protocol.TryParse(payload, out _));
    }

    [Fact]
    public void ParsesLegacyNegativeSequence()
    {
        Span<byte> payload = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload, -42);
        Assert.True(Iperf2Protocol.TryParse(payload, out var datagram));
        Assert.Equal(-42L, datagram.Sequence);
        Assert.False(datagram.Uses64BitSequence);
    }

    private static int ReadInt32(byte[] value, int offset) => BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(offset));
    private static uint ReadUInt32(byte[] value, int offset) => BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset));
}
