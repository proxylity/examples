using System.Net;
using Proxylity.WireGuardClient;

namespace MomentoUdpCache.Sdk;

// ---------------------------------------------------------------------------
//  WgCacheClient  —  drop-in replacement for UdpCacheClient that sends
//  traffic through a Proxylity WireGuard Listener.
//
//  The WireGuard handshake and encryption are handled transparently by
//  WireGuardClient (proxylity/wg-lib). Because the Listener is configured
//  with DecapsulatedDelivery: true, the Lambda receives the same UTF-8 wire
//  protocol payloads as from the plain UDP listener. No VPN setup needed.
//
//  USAGE
//  =====
//  1. Generate a client key pair using the wg CLI (one-time, before deploying):
//
//       wg genkey | tee client_private.key | wg pubkey > client_public.key
//
//     Both files contain a base64-encoded 32-byte key -- the same format
//     this constructor expects. Store client_private.key securely and do not
//     commit it to source control.
//
//  2. Deploy the stack, passing the public key as the SAM parameter:
//
//       # The default ListenerType=both, so no need to override it unless
//       # you want WireGuard only:
//       sam build
//       sam deploy --guided \
//         --parameter-overrides WireGuardClientPublicKey=$(cat client_public.key)
//
//  3. Read outputs from the deployed stack and save to files:
//
//       aws cloudformation describe-stacks --stack-name <stack> \
//         --query "Stacks[0].Outputs[?OutputKey=='CacheEndpointWg'].OutputValue" \
//         --output text
//       aws cloudformation describe-stacks --stack-name <stack> \
//         --query "Stacks[0].Outputs[?OutputKey=='WireGuardPublicKey'].OutputValue" \
//         --output text > server_public.key
//
//  4. Construct the client:
//       await using var cache = new WgCacheClient(
//           IPEndPoint.Parse(endpointFromStack),
//           peerPublicKey: File.ReadAllText("server_public.key").Trim(),
//           myPrivateKey:  File.ReadAllText("client_private.key").Trim());
// ---------------------------------------------------------------------------

public sealed class WgCacheClient : CacheClientBase
{
    private readonly WireGuardClient _wg;

    /// <param name="peerEndpoint">WireGuard Listener endpoint. Use CacheEndpointWg stack output.</param>
    /// <param name="peerPublicKey">Listener's WireGuard public key, base64-encoded. Use WireGuardPublicKey stack output.</param>
    /// <param name="myPrivateKey">Your 32-byte WireGuard private key, base64-encoded.</param>
    public WgCacheClient(IPEndPoint peerEndpoint, string peerPublicKey, string myPrivateKey,
        TimeSpan defaultTtl = default)
        : base(defaultTtl)
    {
        _wg = new WireGuardClient(peerEndpoint,
                  Convert.FromBase64String(peerPublicKey),
                  Convert.FromBase64String(myPrivateKey));
        StartReceiveLoop();
    }

    protected override async Task SendPacketAsync(byte[] packet, CancellationToken ct)
        => await _wg.SendAsync(packet, ct);

    protected override async Task<ReadOnlyMemory<byte>> ReceivePacketAsync(CancellationToken ct)
    {
        var r = await _wg.ReceiveAsync(cancellationToken: ct);
        return r.Buffer;
    }

    protected override void DisposeTransport() => _wg.Dispose();
}
