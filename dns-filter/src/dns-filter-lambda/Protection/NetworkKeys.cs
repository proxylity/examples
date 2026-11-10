using System.Net;

namespace DnsFilterLambda;

public static class NetworkKeys
{
    public static IEnumerable<string> For(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        for (var prefix = bytes.Length * 8; prefix >= 0; prefix--)
        {
            yield return $"IP#{new IPAddress(bytes).ToString().ToLowerInvariant()}/{prefix}";
            if (prefix > 0) bytes[(prefix - 1) / 8] &= (byte)~(1 << (7 - (prefix - 1) % 8));
        }
    }
}
