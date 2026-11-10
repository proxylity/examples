using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DnsBlocklistSync;

public record Source(string Id, string Directory, string Path, bool IsIp = false);
public static class Catalog
{
    public static Source[] For(string profile)
    {
        var (id, path) = profile switch
        {
            "Light" => ("light", "light"),
            "Balanced" => ("normal", "multi"),
            "Strict" => ("pro", "pro"),
            _ => throw new ArgumentException("BLOCKLIST_PROFILE must be Light, Balanced or Strict.")
        };
        return [
            new($"hagezi:{id}", "CODEBUILD_SRC_DIR", $"wildcard/{path}-onlydomains.txt"),
            new("firehol:spamhaus-drop", "CODEBUILD_SRC_DIR_Firehol", "spamhaus_drop.netset", true)
        ];
    }
}

public static class Entries
{
    public static string? Parse(string line, bool ip)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] is '#' or '!' or ';') return null;
        line = line.Split('#', ';')[0].Trim();
        if (ip)
        {
            var parts = line.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) ||
                address.ScopeIdSafe() != 0) throw new FormatException($"Invalid IP entry: {line}");
            var bytes = address.GetAddressBytes();
            if (address.IsIPv4MappedToIPv6) throw new FormatException($"Use an IPv4 CIDR instead of mapped IPv6: {line}");
            var prefix = parts.Length == 1 ? bytes.Length * 8 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (prefix < 0 || prefix > bytes.Length * 8) throw new FormatException($"Invalid prefix: {line}");
            for (var bit = prefix; bit < bytes.Length * 8; bit++)
                bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
            return $"IP#{new IPAddress(bytes).ToString().ToLowerInvariant()}/{prefix}";
        }
        if (line.StartsWith("*.", StringComparison.Ordinal)) line = line[2..];
        line = new IdnMapping().GetAscii(line.TrimEnd('.')).ToLowerInvariant();
        if (line.Length > 253 || line.Length == 0 || IPAddress.TryParse(line, out _) ||
            line.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' ||
                label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new FormatException($"Invalid domain entry: {line}");
        return line;
    }

    private static long ScopeIdSafe(this IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
    public static string Shard(string key) =>
        $"BLOCKLIST_SYNC#{SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] % 16}";
}

public sealed class Snapshot
{
    public int Schema { get; set; } = 2;
    public string Profile { get; set; } = "";
    public Dictionary<string, ListSnapshot> Lists { get; set; } = [];
}
public sealed class ListSnapshot
{
    public Source Source { get; set; } = null!;
    public string Commit { get; set; } = "";
}
public record Change(string Key, string Source, bool Add);
public record DurableState(string Status, Snapshot? Checkpoint, long LastSuccess, string? Error = null, Snapshot? Pending = null);
public record ManagedRecord(string Key, HashSet<string> Sources);
public enum WriteResult { Applied, Protected, Absent }
public interface IStore
{
    Task Acquire(CancellationToken token);
    Task Renew(CancellationToken token);
    Task Release(CancellationToken token);
    Task<DurableState?> ReadState(CancellationToken token);
    Task SaveState(DurableState state, CancellationToken token);
    IAsyncEnumerable<ManagedRecord> ReadManaged(CancellationToken token);
    Task<WriteResult> Apply(Change change, CancellationToken token);
    Task Cleanup(string key, CancellationToken token);
}
