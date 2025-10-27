using System.Net;
using System.Text;

namespace RadiusParserLambda;

internal static partial class RadiusPacketToDictionary
{
    internal static (byte type, string name, string value_type) AttributeType(byte type)
    {
        if (ATTRIBUTE_TYPE_DEFINITIONS.TryGetValue(type, out var entry))
            return (type, entry.name, value_type: entry.type);
        return (type, $"{type}", "string");
    }

    internal static ICollection<string> AttributeTypeValues(string value_type, IEnumerable<ReadOnlyMemory<byte>> values)
    {
        return value_type switch
        {
            "concat" => [ BitConverter.ToString([.. Join(values)]).Replace("-", "") ],
            _ => values.Select(v => AttributeTypeValue(value_type, v)).ToList()
        };
    }

    internal static string AttributeTypeValue(string value_type, ReadOnlyMemory<byte> value)
    {
        return value_type switch
        {
            "text" => Encoding.UTF8.GetString(value.Span),
            "string" => BitConverter.ToString(value.ToArray()).Replace("-", ""),
            "ipv4addr" => new IPAddress(value.Span).ToString(),
            "ipv4prefix" => $"{new IPAddress(value.Span[..4])}/{value.Span[4]}",
            "ipv6addr" => new IPAddress(value.Span).ToString(),
            "ipv6prefix" => $"{new IPAddress(value.Span[..16])}/{value.Span[16]}",
            "ifid" => BitConverter.ToString(value.ToArray()).Replace("-", ":"),
            "integer" => $"{(uint)value.Span[0] << 24 | (uint)value.Span[1] << 16 | (uint)value.Span[2] << 8 | (uint)value.Span[3]}",
            "integer64" => $"{((ulong)value.Span[0] << 56) | ((ulong)value.Span[1] << 48) | ((ulong)value.Span[2] << 40) | ((ulong)value.Span[3] << 32) | ((ulong)value.Span[4] << 24) | ((ulong)value.Span[5] << 16) | ((ulong)value.Span[6] << 8) | (ulong)value.Span[7]}",
            "enum" => $"{(uint)value.Span[0] << 24 | (uint)value.Span[1] << 16 | (uint)value.Span[2] << 8 | (uint)value.Span[3]}",
            "time" => DateTimeOffset.FromUnixTimeSeconds((uint)value.Span[0] << 24 | (uint)value.Span[1] << 16 | (uint)value.Span[2] << 8 | (uint)value.Span[3]).UtcDateTime.ToString("s"),
            "tlv" => BitConverter.ToString(value.ToArray()).Replace("-", ""),
            _ => throw new ArgumentException($"Unsupported value type: {value_type}"),
        };
    }


    internal static IEnumerable<byte> Join(IEnumerable<ReadOnlyMemory<byte>> values)
    {
        foreach (var v in values)
        {
            for (int i = 0; i < v.Length; i++)
            {
                yield return v.Span[i];
            }
        }
    }   

    internal static Dictionary<string, string> AttributesToDictionary(IEnumerable<(byte type, ReadOnlyMemory<byte> value)> attributes, ReadOnlyMemory<byte> shared_secret, ReadOnlyMemory<byte> authenticator)
    {
        var d = attributes
            .GroupBy(a => AttributeType(a.type), a => a.value)
            .ToDictionary(
                g => g.Key.name,
                g => g.Key switch
                {
                    (2, _, _) => DecryptUserPassword(g.First(), shared_secret, authenticator),
                    _ => AttributeTypeValues(g.Key.value_type, g) switch
                    {
                        { Count: 1 } c => c.First(),
                        ICollection<string> v => string.Join("␞", v)
                    }
                }
            );
        return d;
    }

    const int USER_PASSWORD_BLOCK_SIZE = 16;

    public static string DecryptUserPassword(ReadOnlyMemory<byte> encryptedPassword, ReadOnlyMemory<byte> sharedSecret, ReadOnlyMemory<byte> authenticator)
    {
        if (encryptedPassword.Length % USER_PASSWORD_BLOCK_SIZE != 0)
        {
            throw new ArgumentException("Encrypted password length must be a multiple of 16.");
        }

        var decryptedPassword = new byte[encryptedPassword.Length];
        var previousCiphertext = authenticator.ToArray();

        for (int i = 0; i < encryptedPassword.Length; i += USER_PASSWORD_BLOCK_SIZE)
        {
            // Concatenate shared secret and the previous ciphertext (authenticator for the first block)
            var concat = new byte[sharedSecret.Length + previousCiphertext.Length].AsMemory();
            sharedSecret.CopyTo(concat);
            previousCiphertext.CopyTo(concat.Slice(sharedSecret.Length));

            // Calculate the MD5 hash
            var md5Hash = System.Security.Cryptography.MD5.HashData(concat.Span);

            // XOR the encrypted block with the hash
            for (int j = 0; j < USER_PASSWORD_BLOCK_SIZE; j++)
            {
                decryptedPassword[i + j] = (byte)(encryptedPassword.Span[i + j] ^ md5Hash[j]);
            }

            // The current encrypted block becomes the "previous" for the next iteration
            previousCiphertext = new byte[USER_PASSWORD_BLOCK_SIZE];
            encryptedPassword.Span.Slice(i, USER_PASSWORD_BLOCK_SIZE).CopyTo(previousCiphertext);
        }

        // Trim any null-padding from the end of the password
        int lastByteIndex = decryptedPassword.Length - 1;
        while (lastByteIndex >= 0 && decryptedPassword[lastByteIndex] == 0)
        {
            lastByteIndex--;
        }

        return Encoding.UTF8.GetString(decryptedPassword[..(lastByteIndex + 1)]);
    }

    internal static Dictionary<string, string> VendorSpecificAttributesToDictionary(IEnumerable<(uint vendorid, ReadOnlyMemory<byte> value)> vsas)
    {
        var d = vsas
            .GroupBy(v => v.vendorid, v => v.value)
            .ToDictionary(
                g => $"{g.Key}",
                g => string.Join("␞", g.Select(v => BitConverter.ToString(v.ToArray()).Replace("-", "")))
            );
        return d;
    }

    public static Dictionary<byte, (string name, string type)> ATTRIBUTE_TYPE_DEFINITIONS = new Dictionary<byte, (string name, string type)>
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
    };
}