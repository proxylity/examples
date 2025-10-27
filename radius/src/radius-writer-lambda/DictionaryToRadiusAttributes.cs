using System.Net;
using System.Text;
using Microsoft.VisualBasic;

namespace RadiusWriterLambda;

internal static partial class DictionaryToRadiusAttributes
{
    internal static (byte type, string name, string value_type) AttributeType(string name)
    {
        if (ATTRIBUTE_TYPE_DEFINITIONS.TryGetValue(name, out var entry))
            return (entry.code, name, value_type: entry.type);
        return (byte.Parse(name), $"{name}", "string");
    }

    internal static IEnumerable<Memory<byte>> AttributeTypeValues(string value_type, IEnumerable<string> values)
    {
        return value_type switch
        {
            "concat" => JoinFromHex(values).Chunk(253).Select(c => new Memory<byte>(c)),
            _ => [.. values.Select(v => AttributeTypeValue(value_type, v))]
        };
    }

    internal static Memory<byte> AttributeTypeValue(string value_type, string value)
    {
        return value_type switch
        {
            "text" => Encoding.UTF8.GetBytes(value),
            "string" => Convert.FromHexString(value),
            "ipv4addr" or "ipv6addr" => IPAddress.Parse(value).GetAddressBytes(),
            "ipv4prefix" or "ipv6prefix" => [.. IPAddress.Parse(value.Split('/')[0]).GetAddressBytes(), byte.Parse(value.Split('/')[1])],
            "ifid" => Convert.FromHexString(value),
            "integer" => [.. BitConverter.GetBytes(UInt32.Parse(value)).Reverse()],
            "integer64" => [.. BitConverter.GetBytes(Int64.Parse(value)).Reverse()],
            "enum" => [.. BitConverter.GetBytes(UInt32.Parse(value)).Reverse()],
            "time" => [.. BitConverter.GetBytes(DateTimeOffset.Parse(value).ToUnixTimeSeconds()).Reverse()],
            "tlv" => Convert.FromHexString(value),
            _ => throw new ArgumentException($"Unsupported value type: {value_type}"),
        };
    }


    internal static IEnumerable<byte> JoinFromHex(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            var bytes = Convert.FromHexString(v);
            for (int i = 0; i < bytes.Length; i++)
            {
                yield return bytes[i];
            }
        }
    }   

    internal static IEnumerable<(byte type, ReadOnlyMemory<byte> value)> DictionaryToAttributes(Dictionary<string, string> attributes, ReadOnlyMemory<byte> shared_secret, ReadOnlyMemory<byte> authenticator)
    {
        var d = attributes
            .Select(kv => (info: AttributeType(kv.Key), values: kv.Value.Split('␞')))
            .SelectMany(a => AttributeTypeValues(a.info.value_type, a.values).Select(v => (a.info.type, a.info.name, a.info.value_type, value: v)))
            .Select(a => (
                a.type,
                new ReadOnlyMemory<byte>([..a.value.Span])
            ));
        return d;
    }

    public static Dictionary<string, (byte code, string type)> ATTRIBUTE_TYPE_DEFINITIONS = new Dictionary<byte, (string name, string type)>
    {
        { 1, ( "User-Name", "text") },
        { 2, ( "User-Password", "string") },
        { 3, ( "CHAP-Password", "string") },
        { 4, ( "NAS-IP-Address", "ipv4addr") },
        { 5, ( "NAS-Port", "integer") },
        { 6, ( "Service-Type", "enum") },
        { 7, ( "Framed-Protocol", "enum") },
        { 8, ( "Framed-IP-Address", "ipv4addr") },
        { 9, ( "Framed-IP-Netmask", "ipv4addr") },
        { 10, ( "Framed-Routing", "enum") },
        { 11, ( "Filter-Id", "text") },
        { 12, ( "Framed-MTU", "integer") },
        { 13, ( "Framed-Compression", "enum") },
        { 14, ( "Login-IP-Host", "ipv4addr") },
        { 15, ( "Login-Service", "enum") },
        { 16, ( "Login-TCP-Port", "integer") },
        { 18, ( "Reply-Message", "text") },
        { 19, ( "Callback-Number", "text") },
        { 20, ( "Callback-Id", "text") },
        { 22, ( "Framed-Route", "text") },
        { 23, ( "Framed-IPX-Network", "ipv4addr") },
        { 24, ( "State", "string") },
        { 25, ( "Class", "string") },
        { 26, ( "Vendor-Specific", "vsa") },
        { 27, ( "Session-Timeout", "integer") },
        { 28, ( "Idle-Timeout", "integer") },
        { 29, ( "Termination-Action", "enum") },
        { 30, ( "Called-Station-Id", "text") },
        { 31, ( "Calling-Station-Id", "text") },
        { 32, ( "NAS-Identifier", "text") },
        { 33, ( "Proxy-State", "string") },
        { 34, ( "Login-LAT-Service", "text") },
        { 35, ( "Login-LAT-Node", "text") },
        { 36, ( "Login-LAT-Group", "string") },
        { 37, ( "Framed-AppleTalk-Link", "integer") },
        { 38, ( "Framed-AppleTalk-Network", "integer") },
        { 39, ( "Framed-AppleTalk-Zone", "text") },
        { 40, ( "Acct-Status-Type", "enum") },
        { 41, ( "Acct-Delay-Time", "integer") },
        { 42, ( "Acct-Input-Octets", "integer") },
        { 43, ( "Acct-Output-Octets", "integer") },
        { 44, ( "Acct-Session-Id", "text") },
        { 45, ( "Acct-Authentic", "enum") },
        { 46, ( "Acct-Session-Time", "integer") },
        { 47, ( "Acct-Input-Packets", "integer") },
        { 48, ( "Acct-Output-Packets", "integer") },
        { 49, ( "Acct-Terminate-Cause", "enum") },
        { 50, ( "Acct-Multi-Session-Id", "text") },
        { 51, ( "Acct-Link-Count", "integer") },
        { 52, ( "Acct-Input-Gigawords", "integer") },
        { 53, ( "Acct-Output-Gigawords", "integer") },
        { 55, ( "Event-Timestamp", "time") },
        { 56, ( "Egress-VLANID", "integer") },
        { 57, ( "Ingress-Filters", "enum") },
        { 58, ( "Egress-VLAN-Name", "text") },
        { 59, ( "User-Priority-Table", "string") },
        { 60, ( "CHAP-Challenge", "string") },
        { 61, ( "NAS-Port-Type", "enum") },
        { 62, ( "Port-Limit", "integer") },
        { 63, ( "Login-LAT-Port", "text") },
        { 64, ( "Tunnel-Type", "enum") },
        { 65, ( "Tunnel-Medium-Type", "enum") },
        { 66, ( "Tunnel-Client-Endpoint", "text") },
        { 67, ( "Tunnel-Server-Endpoint", "text") },
        { 68, ( "Acct-Tunnel-Connection", "text") },
        { 69, ( "Tunnel-Password", "string") },
        { 70, ( "ARAP-Password", "string") },
        { 71, ( "ARAP-Features", "string") },
        { 72, ( "ARAP-Zone-Access", "enum") },
        { 73, ( "ARAP-Security", "integer") },
        { 74, ( "ARAP-Security-Data", "text") },
        { 75, ( "Password-Retry", "integer") },
        { 76, ( "Prompt", "enum") },
        { 77, ( "Connect-Info", "text") },
        { 78, ( "Configuration-Token", "text") },
        { 79, ( "EAP-Message", "concat") },
        { 80, ( "Message-Authenticator", "string") },
        { 81, ( "Tunnel-Private-Group-ID", "text") },
        { 82, ( "Tunnel-Assignment-ID", "text") },
        { 83, ( "Tunnel-Preference", "integer") },
        { 84, ( "ARAP-Challenge-Response", "string") },
        { 85, ( "Acct-Interim-Interval", "integer") },
        { 86, ( "Acct-Tunnel-Packets-Lost", "integer") },
        { 87, ( "NAS-Port-Id", "text") },
        { 88, ( "Framed-Pool", "text") },
        { 89, ( "CUI", "string") },
        { 90, ( "Tunnel-Client-Auth-ID", "text") },
        { 91, ( "Tunnel-Server-Auth-ID", "text") },
        { 92, ( "NAS-Filter-Rule", "text") },
        { 94, ( "Originating-Line-Info", "string") },
        { 95, ( "NAS-IPv6-Address", "ipv6addr") },
        { 96, ( "Framed-Interface-Id", "ifid") },
        { 97, ( "Framed-IPv6-Prefix", "ipv6prefix") },
        { 98, ( "Login-IPv6-Host", "ipv6addr") },
        { 99, ( "Framed-IPv6-Route", "text") },
        { 100, ( "Framed-IPv6-Pool", "text") },
        { 101, ( "Error-Cause Attribute", "enum") },
        { 102, ( "EAP-Key-Name", "string") },
        { 103, ( "Digest-Response", "text") },
        { 104, ( "Digest-Realm", "text") },
        { 105, ( "Digest-Nonce", "text") },
        { 106, ( "Digest-Response-Auth", "text") },
        { 107, ( "Digest-Nextnonce", "text") },
        { 108, ( "Digest-Method", "text") },
        { 109, ( "Digest-URI", "text") },
        { 110, ( "Digest-Qop", "text") },
        { 111, ( "Digest-Algorithm", "text") },
        { 112, ( "Digest-Entity-Body-Hash", "text") },
        { 113, ( "Digest-CNonce", "text") },
        { 114, ( "Digest-Nonce-Count", "text") },
        { 115, ( "Digest-Username", "text") },
        { 116, ( "Digest-Opaque", "text") },
        { 117, ( "Digest-Auth-Param", "text") },
        { 118, ( "Digest-AKA-Auts", "text") },
        { 119, ( "Digest-Domain", "text") },
        { 120, ( "Digest-Stale", "text") },
        { 121, ( "Digest-HA1", "text") },
        { 122, ( "SIP-AOR", "text") },
        { 123, ( "Delegated-IPv6-Prefix", "ipv6prefix") },
        { 124, ( "MIP6-Feature-Vector", "integer64") },
        { 125, ( "MIP6-Home-Link-Prefix", "string") },
        { 126, ( "Operator-Name", "text") },
        { 127, ( "Location-Information", "string") },
        { 128, ( "Location-Data", "string") },
        { 129, ( "Basic-Location-Policy-Rules", "string") },
        { 130, ( "Extended-Location-Policy-Rules", "string") },
        { 131, ( "Location-Capable", "enum") },
        { 132, ( "Requested-Location-Info", "enum") },
        { 133, ( "Framed-Management-Protocol", "enum") },
        { 134, ( "Management-Transport-Protection", "enum") },
        { 135, ( "Management-Policy-Id", "text") },
        { 136, ( "Management-Privilege-Level", "integer") },
        { 137, ( "PKM-SS-Cert", "concat") },
        { 138, ( "PKM-CA-Cert", "concat") },
        { 139, ( "PKM-Config-Settings", "string") },
        { 140, ( "PKM-Cryptosuite-List", "string") },
        { 141, ( "PKM-SAID", "text") },
        { 142, ( "PKM-SA-Descriptor", "string") },
        { 143, ( "PKM-Auth-Key", "string") },
        { 144, ( "DS-Lite-Tunnel-Name", "string") },
        { 145, ( "Mobile-Node-Identifier", "string") },
        { 146, ( "Service-Selection", "text") },
        { 147, ( "PMIP6-Home-LMA-IPv6-Address", "ipv6addr") },
        { 148, ( "PMIP6-Visited-LMA-IPv6-Address", "ipv6addr") },
        { 149, ( "PMIP6-Home-LMA-IPv4-Address", "ipv4addr") },
        { 150, ( "PMIP6-Visited-LMA-IPv4-Address", "ipv4addr") },
        { 151, ( "PMIP6-Home-HN-Prefix", "ipv6prefix") },
        { 152, ( "PMIP6-Visited-HN-Prefix", "ipv6prefix") },
        { 153, ( "PMIP6-Home-Interface-ID", "ifid") },
        { 154, ( "PMIP6-Visited-Interface-ID", "ifid") },
        { 155, ( "PMIP6-Home-IPv4-HoA", "ipv4prefix") },
        { 156, ( "PMIP6-Visited-IPv4-HoA", "ipv4prefix") },
        { 157, ( "PMIP6-Home-DHCP4-Server-Address", "ipv4addr") },
        { 158, ( "PMIP6-Visited-DHCP4-Server-Address", "ipv4addr") },
        { 159, ( "PMIP6-Home-DHCP6-Server-Address", "ipv6addr") },
        { 160, ( "PMIP6-Visited-DHCP6-Server-Address", "ipv6addr") },
        { 161, ( "PMIP6-Home-IPv4-Gateway", "ipv4addr") },
        { 162, ( "PMIP6-Visited-IPv4-Gateway", "ipv4addr") },
        { 163, ( "EAP-Lower-Layer", "enum") },
        { 164, ( "GSS-Acceptor-Service-Name", "text") },
        { 165, ( "GSS-Acceptor-Host-Name", "text") },
        { 166, ( "GSS-Acceptor-Service-Specifics", "text") },
        { 167, ( "GSS-Acceptor-Realm-Name", "text") },
        { 168, ( "Framed-IPv6-Address", "ipv6addr") },
        { 169, ( "DNS-Server-IPv6-Address", "ipv6addr") },
        { 170, ( "Route-IPv6-Information", "ipv6prefix") },
        { 171, ( "Delegated-IPv6-Prefix-Pool", "text") },
        { 172, ( "Stateful-IPv6-Address-Pool", "text") },
        { 173, ( "IPv6-6rd-Configuration", "tlv") },
        { 174, ( "Allowed-Called-Station-Id", "text") },
        { 175, ( "EAP-Peer-Id", "string") },
        { 176, ( "EAP-Server-Id", "string") },
        { 177, ( "Mobility-Domain-Id", "integer") },
        { 178, ( "Preauth-Timeout", "integer") },
        { 179, ( "Network-Id-Name", "string") },
        { 180, ( "EAPoL-Announcement", "concat") },
        { 181, ( "WLAN-HESSID", "text") },
        { 182, ( "WLAN-Venue-Info", "integer") },
        { 183, ( "WLAN-Venue-Language", "string") },
        { 184, ( "WLAN-Venue-Name", "text") },
        { 185, ( "WLAN-Reason-Code", "integer") },
        { 186, ( "WLAN-Pairwise-Cipher", "integer") },
        { 187, ( "WLAN-Group-Cipher", "integer") },
        { 188, ( "WLAN-AKM-Suite", "integer") },
        { 189, ( "WLAN-Group-Mgmt-Cipher", "integer") },
        { 190, ( "WLAN-RF-Band", "integer") }
    }.ToDictionary(kv => kv.Value.name, kv => (kv.Key, kv.Value.type));
}