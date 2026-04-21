using System.Text;

namespace PacketProcessor;

// ── Application-layer UDP dissection ─────────────────────────────────────────
//
// Detection strategy:
//   1. Try dissectors whose WellKnownPorts contain dstPort (port hint path).
//   2. Then try remaining dissectors via their TryDetect signature.
//   3. Return the first successful (Layer, protocolName) pair, or null.
//
// All dissectors operate on ReadOnlySpan<byte> to avoid allocations during
// the detection phase.

internal static class ApplicationLayer
{
    private static readonly UdpAppDissector[] Dissectors =
    [
        new RadiusDissector(),
        new NtpDissector(),
        new DhcpDissector(),
        new CoapDissector(),
        new SsdpDissector(),
        new DnsDissector(),
        new SyslogDissector(),
    ];

    /// <summary>
    /// Attempt to identify and dissect an application-layer protocol from a
    /// UDP payload.  <paramref name="dstPort"/> may be 0 if unknown (plain-UDP
    /// path where only the source port is available).
    /// </summary>
    internal static (Layer Layer, string ProtocolName)? TryDissect(int dstPort, byte[] data)
    {
        if (data.Length == 0) return null;
        var span = data.AsSpan();

        // Pass 1 — port-hinted dissectors
        if (dstPort > 0)
        {
            foreach (var d in Dissectors)
            {
                if (Array.IndexOf(d.WellKnownPorts, dstPort) >= 0 && d.TryDetect(span))
                    return (d.Dissect(span), d.ProtocolName);
            }
        }

        // Pass 2 — signature-only (all dissectors not already tried, or all if no port hint)
        foreach (var d in Dissectors)
        {
            if (dstPort > 0 && Array.IndexOf(d.WellKnownPorts, dstPort) >= 0)
                continue; // already tried above
            if (d.TryDetect(span))
                return (d.Dissect(span), d.ProtocolName);
        }

        return null;
    }
}

internal abstract class UdpAppDissector
{
    internal abstract int[]  WellKnownPorts { get; }
    internal abstract string ProtocolName   { get; }
    internal abstract bool   TryDetect(ReadOnlySpan<byte> data);
    internal abstract Layer  Dissect(ReadOnlySpan<byte> data);
}

// ── DNS / mDNS ───────────────────────────────────────────────────────────────

internal sealed class DnsDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [53, 5353];
    internal override string ProtocolName   => "DNS";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        // QR bit can be 0 (query) or 1 (response); opcode (bits 14-11) must be 0-5
        int flags  = (data[2] << 8) | data[3];
        int opcode = (flags >> 11) & 0xf;
        if (opcode > 5) return false;
        // At least one question or answer count must be > 0
        int qdcount = (data[4] << 8) | data[5];
        int ancount = (data[6] << 8) | data[7];
        if (qdcount == 0 && ancount == 0) return false;
        // The first byte of the question section must be a valid DNS label start:
        // 0–63 (label length) or 0xC0–0xFF (pointer). Bytes with upper bits 0b10
        // (i.e. 0x80–0xBF) are reserved and indicate non-DNS data.
        if (data.Length > 12)
        {
            byte firstLabel = data[12];
            if (firstLabel > 63 && (firstLabel & 0xC0) != 0xC0) return false;
        }
        return true;
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        int  msgId   = (data[0] << 8) | data[1];
        int  flags   = (data[2] << 8) | data[3];
        bool isResp  = (flags & 0x8000) != 0;
        int  opcode  = (flags >> 11) & 0xf;
        bool aa      = (flags & 0x0400) != 0;
        bool tc      = (flags & 0x0200) != 0;
        bool rd      = (flags & 0x0100) != 0;
        bool ra      = (flags & 0x0080) != 0;
        int  rcode   = flags & 0xf;
        int  qdcount = (data[4] << 8) | data[5];
        int  ancount = (data[6] << 8) | data[7];
        int  nscount = (data[8] << 8) | data[9];
        int  arcount = (data[10] << 8) | data[11];

        string opcodeStr = opcode switch { 0 => "QUERY", 1 => "IQUERY", 2 => "STATUS", 4 => "NOTIFY", 5 => "UPDATE", _ => opcode.ToString() };
        string rcodeStr  = rcode  switch { 0 => "NOERROR", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 4 => "NOTIMP", 5 => "REFUSED", _ => rcode.ToString() };

        // Best-effort: decode the first question name
        string firstName = "";
        if (qdcount > 0 && data.Length > 12)
            firstName = ReadDnsName(data, 12, out _);

        string summary = isResp
            ? $"DNS Response ({rcodeStr}){(firstName.Length > 0 ? " for " + firstName : "")}"
            : $"DNS {opcodeStr}{(firstName.Length > 0 ? " " + firstName : "")}";

        var flagFields = new List<Field>
        {
            new() { Label = "QR",     Value = isResp ? "Response" : "Query" },
            new() { Label = "Opcode", Value = opcodeStr },
            new() { Label = "AA",     Value = aa ? "Set" : "Not set" },
            new() { Label = "TC",     Value = tc ? "Truncated" : "Not truncated" },
            new() { Label = "RD",     Value = rd ? "Set" : "Not set" },
            new() { Label = "RA",     Value = ra ? "Set" : "Not set" },
            new() { Label = "RCODE",  Value = rcodeStr },
        };

        var fields = new List<Field>
        {
            new() { Label = "Transaction ID", Value = $"0x{msgId:x4}" },
            new() { Label = "Flags",          Value = $"0x{flags:x4}", SubFields = [.. flagFields] },
            new() { Label = "Questions",      Value = qdcount.ToString() },
            new() { Label = "Answer RRs",     Value = ancount.ToString() },
            new() { Label = "Authority RRs",  Value = nscount.ToString() },
            new() { Label = "Additional RRs", Value = arcount.ToString() },
        };

        if (firstName.Length > 0)
            fields.Add(new Field { Label = "First Question", Value = firstName });

        return new Layer { Name = "DNS", Summary = summary, Fields = [.. fields] };
    }

    private static string ReadDnsName(ReadOnlySpan<byte> data, int offset, out int end)
    {
        var sb   = new StringBuilder();
        end      = offset;
        int safety = 0;

        while (offset < data.Length && safety++ < 128)
        {
            byte len = data[offset];
            if (len == 0) { end = offset + 1; break; }
            if ((len & 0xc0) == 0xc0)      // pointer
            {
                if (offset + 1 >= data.Length) break;
                int ptr = ((len & 0x3f) << 8) | data[offset + 1];
                end = offset + 2;
                ReadDnsName(data, ptr, out _); // we ignore the pointer-followed name in summary
                break;
            }
            offset++;
            if (sb.Length > 0) sb.Append('.');
            for (int i = 0; i < len && offset < data.Length; i++, offset++)
                sb.Append((char)data[offset]);
        }
        return sb.ToString();
    }
}

// ── NTP ──────────────────────────────────────────────────────────────────────

internal sealed class NtpDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [123];
    internal override string ProtocolName   => "NTP";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 48) return false;
        int version = (data[0] >> 3) & 0x7;
        int mode    = data[0] & 0x7;
        return version is >= 3 and <= 4 && mode is >= 1 and <= 7;
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        int li      = (data[0] >> 6) & 0x3;
        int version = (data[0] >> 3) & 0x7;
        int mode    = data[0] & 0x7;
        int stratum = data[1];
        int poll    = data[2];
        int prec    = (sbyte)data[3];

        string modeStr = mode switch { 1 => "Symmetric Active", 2 => "Symmetric Passive", 3 => "Client", 4 => "Server", 5 => "Broadcast", 6 => "Control", _ => mode.ToString() };
        string liStr   = li switch   { 0 => "No warning", 1 => "Last minute has 61 s", 2 => "Last minute has 59 s", 3 => "Alarm / unsynchronised", _ => li.ToString() };
        string stratumStr = stratum switch { 0 => "Unspecified", 1 => "Primary (GPS/Atomic)", _ => $"Secondary ({stratum})" };

        return new Layer
        {
            Name    = "NTP",
            Summary = $"NTP v{version} {modeStr}, Stratum {stratum}",
            Fields  =
            [
                new() { Label = "Leap Indicator", Value = liStr },
                new() { Label = "Version",        Value = version.ToString() },
                new() { Label = "Mode",           Value = modeStr },
                new() { Label = "Stratum",        Value = stratumStr },
                new() { Label = "Poll Interval",  Value = $"2^{poll} s" },
                new() { Label = "Precision",      Value = $"2^{prec} s" },
            ]
        };
    }
}

// ── CoAP ─────────────────────────────────────────────────────────────────────

internal sealed class CoapDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [5683, 5684];
    internal override string ProtocolName   => "CoAP";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        int ver       = (data[0] >> 6) & 0x3;
        int type      = (data[0] >> 4) & 0x3;
        int tkl       = data[0] & 0xf;
        int codeClass = (data[1] >> 5) & 0x7;
        if (ver != 1 || type > 3) return false;
        // RFC 7252 §3: TKL > 8 is reserved; packet must be long enough for the token
        if (tkl > 8 || data.Length < 4 + tkl) return false;
        // Valid CoAP code classes: 0 (method/empty), 2 (success), 4 (client error), 5 (server error)
        return codeClass is 0 or 2 or 4 or 5;
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        int  ver     = (data[0] >> 6) & 0x3;
        int  type    = (data[0] >> 4) & 0x3;
        int  tkl     = data[0] & 0xf;
        int  codeRaw = data[1];
        int  msgId   = (data[2] << 8) | data[3];

        string typeStr = type switch { 0 => "CON", 1 => "NON", 2 => "ACK", 3 => "RST", _ => type.ToString() };
        string codeStr = codeRaw == 0 ? "Empty" : $"{codeRaw >> 5}.{codeRaw & 0x1f:D2}";

        // Best-effort path extraction from options
        string path = "";
        int offset  = 4 + tkl;
        int optNum  = 0;
        while (offset < data.Length && data[offset] != 0xff)
        {
            int delta = (data[offset] >> 4) & 0xf;
            int olen  = data[offset] & 0xf;
            offset++;
            if (delta == 13)      { delta += data[offset++]; }
            else if (delta == 14) { delta = ((data[offset] << 8) | data[offset + 1]) + 269; offset += 2; }
            if (olen  == 13)      { olen  += data[offset++]; }
            else if (olen  == 14) { olen  = ((data[offset] << 8) | data[offset + 1]) + 269; offset += 2; }
            optNum += delta;
            if (optNum == 11 && offset + olen <= data.Length) // Uri-Path
                path += "/" + Encoding.UTF8.GetString(data.Slice(offset, olen));
            offset += olen;
        }

        return new Layer
        {
            Name    = "CoAP",
            Summary = $"CoAP {typeStr} {codeStr}{(path.Length > 0 ? " " + path : "")}",
            Fields  =
            [
                new() { Label = "Version",        Value = ver.ToString() },
                new() { Label = "Type",           Value = typeStr },
                new() { Label = "Code",           Value = codeStr },
                new() { Label = "Message ID",     Value = $"0x{msgId:x4}" },
                new() { Label = "Token Length",   Value = tkl.ToString() },
                .. (path.Length > 0 ? new[] { new Field { Label = "Uri-Path", Value = path } } : []),
            ]
        };
    }
}

// ── DHCP ─────────────────────────────────────────────────────────────────────

internal sealed class DhcpDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [67, 68];
    internal override string ProtocolName   => "DHCP";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 240) return false;
        if (data[0] is not (1 or 2)) return false;
        // magic cookie at offset 236
        return data[236] == 0x63 && data[237] == 0x82 && data[238] == 0x53 && data[239] == 0x63;
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        string op   = data[0] == 1 ? "BOOTREQUEST" : "BOOTREPLY";
        string htype = data[1] == 1 ? "Ethernet" : data[1].ToString();
        int    hops = data[3];
        int    xid  = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];

        // Decode DHCP Message Type option (53)
        string msgType = "";
        int    offset  = 240;
        while (offset < data.Length)
        {
            byte tag = data[offset++];
            if (tag == 255) break;
            if (tag == 0)   continue;
            if (offset >= data.Length) break;
            byte len = data[offset++];
            if (tag == 53 && len == 1 && offset < data.Length)
            {
                msgType = data[offset] switch
                {
                    1 => "DISCOVER", 2 => "OFFER", 3 => "REQUEST", 4 => "DECLINE",
                    5 => "ACK",      6 => "NAK",   7 => "RELEASE", 8 => "INFORM",
                    _ => data[offset].ToString()
                };
            }
            offset += len;
        }

        // Client IP (yiaddr at offset 16)
        string yiAddr = $"{data[16]}.{data[17]}.{data[18]}.{data[19]}";

        return new Layer
        {
            Name    = "DHCP",
            Summary = $"DHCP {(msgType.Length > 0 ? msgType : op)}",
            Fields  =
            [
                new() { Label = "Operation",      Value = op },
                new() { Label = "HW Type",        Value = htype },
                new() { Label = "Hops",           Value = hops.ToString() },
                new() { Label = "Transaction ID", Value = $"0x{xid:x8}" },
                new() { Label = "Your IP",        Value = yiAddr },
                .. (msgType.Length > 0 ? new[] { new Field { Label = "Message Type", Value = msgType } } : []),
            ]
        };
    }
}

// ── SSDP ─────────────────────────────────────────────────────────────────────

internal sealed class SsdpDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [1900];
    internal override string ProtocolName   => "SSDP";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        string head = Encoding.ASCII.GetString(data[..Math.Min(data.Length, 16)]);
        return head.StartsWith("M-SEARCH", StringComparison.Ordinal)
            || head.StartsWith("NOTIFY",   StringComparison.Ordinal)
            || head.StartsWith("HTTP/",    StringComparison.Ordinal);
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        string text    = Encoding.UTF8.GetString(data);
        string firstLine = text.Split('\n', 2)[0].Trim();

        // Extract a handful of useful SSDP headers
        var fields = new List<Field> { new() { Label = "Request Line", Value = firstLine } };
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 1) continue;
            string hdr = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            if (hdr.Equals("ST", StringComparison.OrdinalIgnoreCase)
             || hdr.Equals("NT", StringComparison.OrdinalIgnoreCase)
             || hdr.Equals("USN", StringComparison.OrdinalIgnoreCase)
             || hdr.Equals("Location", StringComparison.OrdinalIgnoreCase))
                fields.Add(new Field { Label = hdr, Value = val });
        }

        return new Layer { Name = "SSDP", Summary = $"SSDP {firstLine}", Fields = [.. fields] };
    }
}

// ── Syslog ───────────────────────────────────────────────────────────────────

internal sealed class SyslogDissector : UdpAppDissector
{
    internal override int[]  WellKnownPorts => [514];
    internal override string ProtocolName   => "Syslog";

    internal override bool TryDetect(ReadOnlySpan<byte> data) =>
        data.Length > 3 && data[0] == '<';

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);

        // Parse PRI: <NNN>
        int    priEnd    = text.IndexOf('>');
        int    pri       = priEnd > 1 && int.TryParse(text[1..priEnd], out int p) ? p : -1;
        string facility  = pri >= 0 ? FacilityName(pri >> 3)  : "?";
        string severity  = pri >= 0 ? SeverityName(pri & 0x7) : "?";
        string remainder = pri >= 0 && priEnd + 1 < text.Length ? text[(priEnd + 1)..].Trim() : text;
        string firstLine = remainder.Split('\n')[0].Trim();

        return new Layer
        {
            Name    = "Syslog",
            Summary = $"Syslog {severity} ({facility}): {firstLine[..Math.Min(firstLine.Length, 80)]}",
            Fields  =
            [
                new() { Label = "Facility",  Value = facility },
                new() { Label = "Severity",  Value = severity },
                new() { Label = "Message",   Value = firstLine },
            ]
        };
    }

    private static string FacilityName(int f) => f switch
    {
        0 => "kern", 1 => "user", 2 => "mail", 3 => "daemon",
        4 => "auth", 5 => "syslog", 6 => "lpr", 7 => "news",
        16 => "local0", 17 => "local1", 18 => "local2", 19 => "local3",
        20 => "local4", 21 => "local5", 22 => "local6", 23 => "local7",
        _ => f.ToString()
    };

    private static string SeverityName(int s) => s switch
    {
        0 => "Emergency", 1 => "Alert", 2 => "Critical", 3 => "Error",
        4 => "Warning",   5 => "Notice", 6 => "Info",    7 => "Debug",
        _ => s.ToString()
    };
}

// ── RADIUS ───────────────────────────────────────────────────────────────────

internal sealed class RadiusDissector : UdpAppDissector
{
    // RFC 2865 / 2866 ports; legacy 1645/1646 still in common use
    internal override int[]  WellKnownPorts => [1812, 1813, 1645, 1646];
    internal override string ProtocolName   => "RADIUS";

    internal override bool TryDetect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return false;
        // Code must be a known RADIUS code
        if (!IsKnownCode(data[0])) return false;
        // Length field (big-endian uint16 at offset 2) must equal the packet length
        int declared = (data[2] << 8) | data[3];
        return declared >= 20 && declared <= 4096 && declared <= data.Length;
    }

    internal override Layer Dissect(ReadOnlySpan<byte> data)
    {
        byte   code       = data[0];
        byte   identifier = data[1];
        int    length     = (data[2] << 8) | data[3];
        string codeStr    = CodeName(code);

        // Authenticator is 16 bytes at offset 4 — show as hex
        var authSb = new StringBuilder();
        for (int i = 4; i < 20 && i < data.Length; i++) authSb.Append($"{data[i]:x2}");

        var attrFields = new List<Field>();
        int offset = 20;
        while (offset + 2 <= length && offset + 2 <= data.Length)
        {
            byte attrType = data[offset];
            byte attrLen  = data[offset + 1];
            if (attrLen < 2 || offset + attrLen > data.Length) break;

            var valSpan = data.Slice(offset + 2, attrLen - 2);
            string attrName  = AttrName(attrType);
            string attrValue = FormatAttrValue(attrType, valSpan);
            attrFields.Add(new Field { Label = $"{attrName} ({attrType})", Value = attrValue });
            offset += attrLen;
        }

        var fields = new List<Field>
        {
            new() { Label = "Code",          Value = $"{codeStr} ({code})" },
            new() { Label = "Identifier",    Value = identifier.ToString() },
            new() { Label = "Length",        Value = length.ToString() },
            new() { Label = "Authenticator", Value = authSb.ToString() },
        };
        if (attrFields.Count > 0)
            fields.Add(new Field { Label = "Attributes", Value = $"{attrFields.Count} attribute(s)", SubFields = [.. attrFields] });

        return new Layer
        {
            Name    = "RADIUS",
            Summary = $"RADIUS {codeStr} (id={identifier})",
            Fields  = [.. fields]
        };
    }

    private static bool IsKnownCode(byte code) => code switch
    {
        1 or 2 or 3 or 4 or 5 or 11 or 12 or 13 => true,   // Access-*/Accounting-*/Status-*
        40 or 41 or 42 or 43 or 44 or 45         => true,   // Disconnect-*/CoA-*
        _ => false
    };

    private static string CodeName(byte code) => code switch
    {
        1  => "Access-Request",
        2  => "Access-Accept",
        3  => "Access-Reject",
        4  => "Accounting-Request",
        5  => "Accounting-Response",
        11 => "Access-Challenge",
        12 => "Status-Server",
        13 => "Status-Client",
        40 => "Disconnect-Request",
        41 => "Disconnect-ACK",
        42 => "Disconnect-NAK",
        43 => "CoA-Request",
        44 => "CoA-ACK",
        45 => "CoA-NAK",
        _  => $"Unknown ({code})"
    };

    private static string AttrName(byte t) => t switch
    {
        1  => "User-Name",
        2  => "User-Password",
        4  => "NAS-IP-Address",
        5  => "NAS-Port",
        6  => "Service-Type",
        7  => "Framed-Protocol",
        8  => "Framed-IP-Address",
        11 => "Filter-Id",
        18 => "Reply-Message",
        25 => "Class",
        26 => "Vendor-Specific",
        30 => "Called-Station-Id",
        31 => "Calling-Station-Id",
        32 => "NAS-Identifier",
        40 => "Acct-Status-Type",
        41 => "Acct-Delay-Time",
        42 => "Acct-Input-Octets",
        43 => "Acct-Output-Octets",
        44 => "Acct-Session-Id",
        61 => "NAS-Port-Type",
        79 => "EAP-Message",
        80 => "Message-Authenticator",
        _  => $"Attr-{t}"
    };

    private static string FormatAttrValue(byte type, ReadOnlySpan<byte> val)
    {
        // Attributes that are human-readable UTF-8 strings
        if (type is 1 or 11 or 18 or 30 or 31 or 32 or 44)
            return Encoding.UTF8.GetString(val);

        // Attributes that are 4-byte IPv4 addresses
        if (type is 4 or 8 && val.Length == 4)
            return $"{val[0]}.{val[1]}.{val[2]}.{val[3]}";

        // Attributes that are 4-byte integers
        if (type is 5 or 6 or 7 or 40 or 41 or 42 or 43 or 61 && val.Length == 4)
            return ((uint)((val[0] << 24) | (val[1] << 16) | (val[2] << 8) | val[3])).ToString();

        // Message-Authenticator and EAP — show as hex
        var sb = new StringBuilder();
        foreach (byte b in val) sb.Append($"{b:x2}");
        return sb.ToString();
    }
}
