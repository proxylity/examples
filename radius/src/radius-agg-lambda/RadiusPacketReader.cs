namespace RadiusAcctLambda;

public static class RadiusPacketReader
{
    internal static Span<byte> FindAttributeValue(Span<byte> packet, byte attrType)
    {
        int pos = 20;
        while (pos + 2 <= packet.Length)
        {
            byte type = packet[pos];
            byte length = packet[pos + 1];
            if (length < 2 || pos + length > packet.Length)
                return [];

            if (type == attrType)
                return packet.Slice(pos + 2, length - 2);
            pos += length;
        }
        return [];
    }

    internal static IEnumerable<(byte type, Memory<byte> value)> Attributes(Memory<byte> packet)
    {
        if (packet.Length < 20)
            yield break;

        int pos = 20;
        while (pos + 2 <= packet.Length)
        {
            byte type = packet.Span[pos];
            byte length = packet.Span[pos + 1];
            if (length < 2 || pos + length > packet.Length)
                yield break;

            yield return (type, packet.Slice(pos + 2, length - 2));
            pos += length;
        }
    }

    public static (byte code, short length, byte id, Memory<byte> authenticator, IEnumerable<(byte type, Memory<byte> value)> attributes) Parse(Memory<byte> packet)
    {
        if (packet.Length < 20)
            throw new ArgumentException("Invalid RADIUS packet");

        byte code = packet.Span[0];
        short length = (short)((packet.Span[2] << 8) | packet.Span[3]);
        byte id = packet.Span[1];
        var authenticator = packet[4..20];
        var attributes = Attributes(packet);

        return (code, length, id, authenticator, attributes);
    }

    public static bool? CheckMessageAuthenticator(Memory<byte> packet, ReadOnlyMemory<byte> shared_secret)
    {
        Span<byte> hash_input = stackalloc byte[packet.Length];
        packet.Span.CopyTo(hash_input);

        // Find Message-Authenticator attribute (type 80)
        var value_to_clear = FindAttributeValue(hash_input, 80);
        if (value_to_clear.IsEmpty)
        {
            // No Message-Authenticator attribute found - this is acceptable for basic RADIUS
            // radtest typically doesn't include Message-Authenticator unless specifically configured
            return null;
        }

        value_to_clear.Clear(); // zero out Message-Authenticator field in the hash input

        // Calculate HMAC-MD5 over the packet with Message-Authenticator zeroed out
        using var hmac = new System.Security.Cryptography.HMACMD5(shared_secret.ToArray());
        var computed_hash = hmac.ComputeHash(hash_input.ToArray());

        var reference_value = FindAttributeValue(packet.Span, 80); // the Message-Authenticator (80) attribute from the packet
        return reference_value.SequenceEqual(computed_hash);
    }
}
