using System.Buffers.Binary;

namespace CoapDemo.Common.Protocol;

/// <summary>
/// Helpers for encoding/decoding CoAP option values, which the Proxylity "coap" formatter
/// carries as base64 strings over the minimal big-endian byte representation defined by
/// RFC 7252 §3.2 (uint) and RFC 7959 §2.1 (Block1/Block2).
/// </summary>
public static class CoapCodec
{
    /// <summary>Encodes a non-negative integer as the minimal big-endian byte string (RFC 7252 §3.2). Zero encodes to an empty value.</summary>
    public static string UIntToBase64(long value)
    {
        if (value == 0) return "";
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, (ulong)value);
        var trimmed = ((ReadOnlySpan<byte>)buf).TrimStart((byte)0);
        return Convert.ToBase64String(trimmed);
    }

    /// <summary>Decodes a base64-encoded minimal big-endian uint option value. A null/empty value decodes to zero.</summary>
    public static long Base64ToUInt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var bytes = Convert.FromBase64String(value);
        long result = 0;
        foreach (var b in bytes) result = (result << 8) | b;
        return result;
    }

    /// <summary>Encodes a Block1/Block2 option value: NUM (block number) &lt;&lt; 4 | M (more) &lt;&lt; 3 | SZX (size exponent) (RFC 7959 §2.1).</summary>
    public static string EncodeBlockOption(int blockNumber, bool more, int szx)
    {
        long value = ((long)blockNumber << 4) | (uint)(more ? 0b1000 : 0) | (uint)(szx & 0x7);
        return UIntToBase64(value);
    }

    /// <summary>Decodes a Block1/Block2 option value into (BlockNumber, More, Szx, BlockSizeBytes).</summary>
    public static (int BlockNumber, bool More, int Szx, int BlockSizeBytes) DecodeBlockOption(string? value)
    {
        var raw = Base64ToUInt(value);
        var szx = (int)(raw & 0x7);
        var more = (raw & 0b1000) != 0;
        var num = (int)(raw >> 4);
        return (num, more, szx, SzxToBlockSize(szx));
    }

    /// <summary>Converts a Block SZX exponent (0-6) to a block size in bytes (2^(SZX+4): 16..1024).</summary>
    public static int SzxToBlockSize(int szx) => 1 << (Math.Clamp(szx, 0, 6) + 4);

    /// <summary>Converts a block size in bytes to the largest valid SZX exponent that does not exceed it.</summary>
    public static int BlockSizeToSzx(int blockSizeBytes)
    {
        for (var szx = 6; szx >= 0; szx--)
            if (SzxToBlockSize(szx) <= blockSizeBytes) return szx;
        return 0;
    }
}
