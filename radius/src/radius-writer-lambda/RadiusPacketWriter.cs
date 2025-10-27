namespace RadiusWriterLambda;

public static class RadiusPacketWriter
{
    public static byte[] CreateResponsePacket(byte code, byte id, ReadOnlyMemory<byte> request_authenticator, IEnumerable<(byte type, ReadOnlyMemory<byte> value)> attributes, ReadOnlyMemory<byte> shared_secret)
    {
        // calculate length of attributes
        int attributes_length = attributes.Sum(attr => 2 + attr.value.Length);

        int packet_length = 20 + attributes_length + 18; // extra 18 bytes for Message-Authenticator attribute
        Span<byte> packet = stackalloc byte[packet_length + shared_secret.Length];

        packet[0] = code; // code
        packet[1] = id; // id
        packet[2] = (byte)(packet_length >> 8); // length high byte
        packet[3] = (byte)(packet_length & 0xFF); // length low byte

        request_authenticator.Span.CopyTo(packet.Slice(4, 16)); // request authenticator for hash calculation, will be replaced with response authenticator later

        int pos = 20;
        foreach (var (type, value) in attributes)
        {
            packet[pos] = type;
            packet[pos + 1] = (byte)(2 + value.Length);
            value.Span.CopyTo(packet[(pos + 2)..]);
            pos += 2 + value.Length;
        }

        // Add the Message-Authenticator attribute (zeroed initially)
        packet[pos] = 80; // Message-Authenticator (80) attribute type
        packet[pos + 1] = 18; // length
        packet.Slice(pos + 2, 16).Clear(); // zero out Message-Authenticator field

        // Calculate HMAC-MD5 over the entire packet with Message-Authenticator zeroed
        // but with the Response Authenticator now in place
        using var hmac = new System.Security.Cryptography.HMACMD5([..shared_secret.Span]);
        var messageAuth = hmac.ComputeHash(packet[..packet_length].ToArray());
        
        // Copy the calculated Message-Authenticator back
        messageAuth.CopyTo(packet.Slice(pos + 2, 16));

        // append the shared secret for the response authenticator calculation
        shared_secret.Span.CopyTo(packet.Slice(packet_length, shared_secret.Length));

        // now calculate Response Authenticator
        var response_authenticator = System.Security.Cryptography.MD5.HashData(packet);

        // Set the Response Authenticator in the packet (replaces Request Authenticator)
        response_authenticator.CopyTo(packet.Slice(4, 16));

        return packet[..packet_length].ToArray();
    }
}