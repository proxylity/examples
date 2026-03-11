using System.Net;
using System.Net.Sockets;

namespace MomentoUdpCache.Sdk;

public sealed class UdpCacheClient : CacheClientBase
{
    private readonly UdpClient _socket;

    public UdpCacheClient(string host, int port)
    {
        _socket = new UdpClient();
        _socket.Connect(new IPEndPoint(Dns.GetHostAddresses(host).First(), port));
        StartReceiveLoop();
    }

    protected override async Task SendPacketAsync(byte[] packet, CancellationToken ct)
    {
        await _socket.SendAsync(packet, ct);
    }

    protected override async Task<ReadOnlyMemory<byte>> ReceivePacketAsync(CancellationToken ct)
    {
        var r = await _socket.ReceiveAsync(ct);
        return r.Buffer;
    }

    protected override void DisposeTransport() => _socket.Dispose();
}
