using PacketDotNet;
using Proxylity.UdpGateway.LambdaSdk;

namespace WireGuardEchoLambda;

public class ExampleDecapsulatedHandler : DecapsulatedHandler
{
    private const int NtpPacketSize = 48;
    private const byte ServerMode = 4;
    private const byte ClientMode = 3;
    private static readonly DateTime NtpEpoch =
        new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // echo UDP
    public override async Task<byte[]?> HandleAsync(PacketDotNet.UdpPacket packet, RemoteEndpoint remote, Amazon.Lambda.Core.ILambdaLogger logger)
    {
        logger.LogLine($"Received tunneled UDP packet from {remote.IpAddress}:{remote.Port}");
        if (packet.ParentPacket is not PacketDotNet.IPPacket ip)
        {
            logger.LogLine("Received UDP packet without a parent IP packet.");
            return null;
        }

        if (packet.DestinationPort == 123)
        {
            return await HandleNtpAsync(packet, remote, logger);
        }

        var text = System.Text.Encoding.UTF8.GetString(packet.PayloadData);
        var echo = $"Hi {remote.IpAddress}:{remote.Port}! Your message \"{text}\" for {ip.DestinationAddress}:{packet.DestinationPort} has been received.";
        PacketDotNet.IPPacket parent = ip.Version switch
        {
            PacketDotNet.IPVersion.IPv4 => new PacketDotNet.IPv4Packet(ip.DestinationAddress, ip.SourceAddress),
            PacketDotNet.IPVersion.IPv6 => new PacketDotNet.IPv6Packet(ip.DestinationAddress, ip.SourceAddress),
            _ => throw new NotSupportedException($"IP version {ip.Version} UDP is not supported.")
        };
        var udp = new PacketDotNet.UdpPacket(packet.DestinationPort, packet.SourcePort)
        {
            PayloadData = System.Text.Encoding.UTF8.GetBytes(echo)
        };
        parent.PayloadPacket = udp;
        if (parent is PacketDotNet.IPv4Packet ip4) ip4.UpdateIPChecksum();
        udp.UpdateUdpChecksum();

        return await Task.FromResult(parent.Bytes);
    }

    private async Task<byte[]?> HandleNtpAsync(UdpPacket packet, RemoteEndpoint remote, Amazon.Lambda.Core.ILambdaLogger logger)
    {
        var request = packet.PayloadData;
        if (request.Length < NtpPacketSize)
            return null;

        byte liVnMode = request[0];
        byte version = (byte)((liVnMode >> 3) & 0b111);
        byte mode = (byte)(liVnMode & 0b111);

        // Only answer client mode
        if (mode != ClientMode || version < 3)
            return null;

        var response = new byte[NtpPacketSize];

        // LI = 0, VN = same as client, Mode = Server
        response[0] = (byte)((0 << 6) | (version << 3) | ServerMode);

        response[1] = 2;      // Stratum 2
        response[2] = 6;      // Poll (reasonable default)
        response[3] = unchecked((byte)-20); // Precision (~1 µs)

        // Root Delay & Dispersion (fixed-point, 16.16)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(8), 0x00010000); // ~1ms

        // Reference ID: "LOCL"
        response[12] = (byte)'L';
        response[13] = (byte)'O';
        response[14] = (byte)'C';
        response[15] = (byte)'L';

        DateTime now = DateTime.UtcNow;

        // Reference Timestamp
        write_timestamp(response, 16, now);

        // Originate Timestamp = client's Transmit Timestamp
        Buffer.BlockCopy(request, 40, response, 24, 8);

        // Receive Timestamp
        write_timestamp(response, 32, now);

        // Transmit Timestamp
        write_timestamp(response, 40, now);

        return response;
        
        static void write_timestamp(byte[] buffer, int offset, DateTime utc)
        {
            ulong seconds = (ulong)(utc - NtpEpoch).TotalSeconds;
            ulong fraction = (ulong)(
                (utc.Ticks % TimeSpan.TicksPerSecond) *
                ((double)(1UL << 32) / TimeSpan.TicksPerSecond));

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                buffer.AsSpan(offset),
                (uint)seconds);

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                buffer.AsSpan(offset + 4),
                (uint)fraction);
        }
    }

    // respond to ICMP pings
    public override async Task<byte[]?> HandleAsync(PacketDotNet.IcmpV4Packet packet, RemoteEndpoint remote, Amazon.Lambda.Core.ILambdaLogger logger)
    {
        if (packet.ParentPacket is not PacketDotNet.IPv4Packet ip)
        {
            logger.LogLine("Received ICMP packet without a parent IPv4 packet.");
            return null;
        }
        var parent = new PacketDotNet.IPv4Packet(ip.DestinationAddress, ip.SourceAddress)
        {
            Id = 0,
            TimeToLive = 64,
            FragmentFlags = 0x02, // Don't Fragment flag
            Protocol = PacketDotNet.ProtocolType.Icmp
        };
        var pong = new PacketDotNet.IcmpV4Packet(new PacketDotNet.Utils.ByteArraySegment(new byte[8 + packet.PayloadData.Length]))
        {
            TypeCode = PacketDotNet.IcmpV4TypeCode.EchoReply,
            Id = packet.Id,
            Sequence = packet.Sequence,
            PayloadData = packet.PayloadData
        };
        parent.PayloadPacket = pong;
        parent.UpdateIPChecksum();
        pong.UpdateIcmpChecksum();

        return await Task.FromResult(parent.Bytes);
    }
}