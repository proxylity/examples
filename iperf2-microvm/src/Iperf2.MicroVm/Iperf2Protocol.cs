using System.Buffers.Binary;

namespace Iperf2.MicroVm;

/// <summary>The fixed part of an iperf2 UDP datagram that this example cares about.</summary>
public readonly record struct IperfDatagram(long Sequence, long SentMicroseconds, bool Uses64BitSequence)
{
    /// <summary>The client ends a test by sending datagrams with a negative sequence number.</summary>
    public bool IsFinal => Sequence < 0;
    public long AbsoluteSequence => Sequence == long.MinValue ? long.MaxValue : Math.Abs(Sequence);
}

/// <summary>The numbers the server reports back to the client when the test ends.</summary>
public readonly record struct SessionReport(
    long ReceivedBytes,
    long DurationMicroseconds,
    long LostDatagrams,
    long OutOfOrderDatagrams,
    long Datagrams,
    long JitterMicroseconds);

/// <summary>
/// The slice of the iperf2 (2.1.x / 2.2.x) UDP wire protocol needed to act as a server.
/// Layouts were verified by capturing a stock client and server (see scripts/research).
/// </summary>
public static class Iperf2Protocol
{
    // Legacy: 32-bit id, seconds, microseconds. Current: the same plus the high 32 bits of the id.
    public const int LegacyDatagramHeaderSize = 12;
    public const int DatagramHeaderSize = 16;
    public const int LegacyServerHeaderSize = 100;
    public const int ServerHeaderSize = 112;

    /// <summary>Smallest prefix of a datagram that <see cref="TryParse"/> needs (header plus the first-packet flags word).</summary>
    public const int ParsePrefixSize = 20;

    private const uint HeaderVersion1 = 0x80000000;
    private const uint HeaderSequence64Bit = 0x08000000;

    public static bool TryParse(ReadOnlySpan<byte> payload, out IperfDatagram datagram)
    {
        datagram = default;
        if (payload.Length < LegacyDatagramHeaderSize) return false;

        var sequenceLow = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        var microseconds = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        if (microseconds >= 1_000_000) return false;

        var sequenceHigh = payload.Length >= DatagramHeaderSize
            ? BinaryPrimitives.ReadUInt32BigEndian(payload[12..])
            : 0;
        var firstPacketFlags = payload.Length >= DatagramHeaderSize + sizeof(uint)
            ? BinaryPrimitives.ReadUInt32BigEndian(payload[16..])
            : 0;

        // A 64-bit sender sets the high word to all ones on the final datagram, and sets a flag in
        // the first datagrams. Everything else keeps the high word at zero, so the 32-bit
        // interpretation gives the same value.
        var uses64BitSequence = (sequenceLow <= 1 && (firstPacketFlags & HeaderSequence64Bit) != 0)
            || (sequenceHigh == uint.MaxValue && (sequenceLow & 0x80000000) != 0);
        var sequence = uses64BitSequence
            ? unchecked((long)(((ulong)sequenceHigh << 32) | sequenceLow))
            : unchecked((int)sequenceLow);

        datagram = new(sequence, checked((long)seconds * 1_000_000 + microseconds), uses64BitSequence);
        return true;
    }

    /// <summary>
    /// Builds the server report that answers a final datagram. A stock server sends exactly
    /// header + report (128 bytes for the 64-bit layout, 112 for the legacy layout) with the
    /// datagram header zeroed, and so do we.
    /// </summary>
    public static byte[] WriteServerReport(bool uses64BitSequence, SessionReport report)
    {
        var datagramHeaderSize = uses64BitSequence ? DatagramHeaderSize : LegacyDatagramHeaderSize;
        var serverHeaderSize = uses64BitSequence ? ServerHeaderSize : LegacyServerHeaderSize;
        var response = new byte[datagramHeaderSize + serverHeaderSize];

        var header = response.AsSpan(datagramHeaderSize, serverHeaderSize);
        WriteUInt32(header, 0, HeaderVersion1 | (uses64BitSequence ? HeaderSequence64Bit : 0));
        WriteUInt32(header, 4, (uint)((ulong)report.ReceivedBytes >> 32));
        WriteUInt32(header, 8, (uint)report.ReceivedBytes);
        WriteTime(header, 12, report.DurationMicroseconds);
        WriteUInt32(header, 20, (uint)report.LostDatagrams);
        WriteUInt32(header, 24, (uint)report.OutOfOrderDatagrams);
        WriteUInt32(header, 28, (uint)report.Datagrams);
        WriteTime(header, 32, report.JitterMicroseconds);
        WriteInt32(header, 92, report.DurationMicroseconds > 0
            ? (int)Math.Min(int.MaxValue, report.Datagrams * 1_000_000 / report.DurationMicroseconds)
            : 0);
        WriteInt32(header, 96, 1);
        if (uses64BitSequence)
        {
            WriteUInt32(header, 100, (uint)((ulong)report.LostDatagrams >> 32));
            WriteUInt32(header, 104, (uint)((ulong)report.OutOfOrderDatagrams >> 32));
            WriteUInt32(header, 108, (uint)((ulong)report.Datagrams >> 32));
        }
        return response;
    }

    private static void WriteTime(Span<byte> destination, int offset, long microseconds)
    {
        var value = Math.Max(0, microseconds);
        WriteInt32(destination, offset, (int)Math.Min(int.MaxValue, value / 1_000_000));
        WriteInt32(destination, offset + 4, (int)(value % 1_000_000));
    }

    private static void WriteInt32(Span<byte> destination, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(destination[offset..], value);

    private static void WriteUInt32(Span<byte> destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], value);
}
