using Proxylity.UdpGateway.LambdaSdk;

namespace WireGuardEchoLambda;

public class ExampleDecapsulatedHandler : DecapsulatedHandler
{
    // echo UDP
    public override async Task<byte[]?> HandleAsync(PacketDotNet.UdpPacket packet, RemoteEndpoint remote, Amazon.Lambda.Core.ILambdaLogger logger)
    {
        logger.LogLine($"Received tunneled UDP packet from {remote.IpAddress}:{remote.Port}");
        if (packet.ParentPacket is not PacketDotNet.IPPacket ip)
        {
            logger.LogLine("Received UDP packet without a parent IP packet.");
            return null;
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
