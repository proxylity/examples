namespace RadiusAcctLambda;

public static class RadiusPacketWriter
{
    public static byte[] CreateResponsePacket(byte code, byte id, ReadOnlyMemory<byte> request_authenticator, IEnumerable<(byte type, Memory<byte> value)> attributes, ReadOnlyMemory<byte> shared_secret)
    {
        // calculate length of attributes
        int attributes_length = attributes.Sum(attr => 2 + attr.value.Length);

        int packet_length = 20 + attributes_length;
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

        // Find the Message-Authenticator (80) attribute, if included
        var ma = attributes.FirstOrDefault(attr => attr.type == 80);
        if (ma.value.Length == 16)
        {
            // Calculate HMAC-MD5 over the entire packet with Message-Authenticator zeroed
            // but with the Response Authenticator now in place
            using var hmac = new System.Security.Cryptography.HMACMD5([.. shared_secret.Span]);
            var messageAuth = hmac.ComputeHash(packet[..packet_length].ToArray());

            // Copy the calculated Message-Authenticator back
            messageAuth.AsMemory().CopyTo(ma.value);
        }

        // append the shared secret for the response authenticator calculation
        shared_secret.Span.CopyTo(packet.Slice(packet_length, shared_secret.Length));

        // now calculate Response Authenticator
        var response_authenticator = System.Security.Cryptography.MD5.HashData(packet);

        // Set the Response Authenticator in the packet (replaces Request Authenticator)
        response_authenticator.CopyTo(packet.Slice(4, 16));

        return packet[..packet_length].ToArray();
    }
}