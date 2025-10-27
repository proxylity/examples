namespace RadiusAcctLambda;

public class Handler : Proxylity.UdpGateway.LambdaSdk.IUdpGatewayHandlerAsync
{
    static readonly ReadOnlyMemory<byte> RADIUS_SHARED_SECRET = System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable(nameof(RADIUS_SHARED_SECRET)) ?? "not-so-secret");

    public async Task<byte[]?> HandleAsync(Proxylity.UdpGateway.LambdaSdk.UdpGatewayMessage message, Amazon.Lambda.Core.ILambdaLogger logger)
    {
        logger.LogLine($"Received raw packet of {message.Data.Length} bytes from {message.Remote.IpAddress}:{message.Remote.Port}");
        try
        {
            var packet = message.Data.AsMemory();
            var valid = RadiusPacketReader.CheckMessageAuthenticator(packet, RADIUS_SHARED_SECRET);
            if (valid == false)
            {
                logger.LogLine($"Inauthentic RADIUS packet");
                return null;
            }

            var (code, length, id, authenticator, attributes, vsas) = RadiusPacketReader.Parse(packet);
            logger.LogLine($"Parsed RADIUS packet: valid={valid}, code={code}, length={length}, id={id}, authenticator={BitConverter.ToString(authenticator.ToArray()).Replace("-", "")}");

            if (code != 4) // Accounting-Request
            {
                logger.LogLine($"Ignoring non-accounting RADIUS packet with code {code}");
                return null;
            }

            var response_attributes = attributes.Where(attr => attr.type == 33) // copy Proxy-State (33) attributes
                .Concat([ ((byte)55, BitConverter.GetBytes(Environment.TickCount)) ]) // Event-Timestamp (55) for a little unpredictability
                .Concat(valid == true ? [(80, new byte[16])] : []); // Message-Authenticator (80) if present in request

            var response_data = RadiusPacketWriter.CreateResponsePacket((byte)5, id, authenticator, response_attributes, RADIUS_SHARED_SECRET); // add Message-Authenticator (80) if present in request
            return response_data;
        }
        catch (Exception ex)
        {
            logger.LogLine($"Failed to parse RADIUS packet {message.Tag}: {ex}");
        }
        return await Task.FromResult<byte[]?>(null);
    }
}