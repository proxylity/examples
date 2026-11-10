using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DnsBlocklistSync;

public sealed class GitSource
{
    private sealed class GitException(string message, int exitCode) : IOException(message)
    {
        public int ExitCode { get; } = exitCode;
    }
    public long ParsedLines { get; private set; }
    private static string Repo(Source source)
    {
        var variable = source.Directory;
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(directory) && Directory.Exists(source.Directory)) directory = source.Directory;
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException($"{variable} must identify the CodeBuild source checkout.");
        return directory;
    }

    private static async IAsyncEnumerable<string> Run(string? directory, string[] args,
        [EnumeratorCancellation] CancellationToken token)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        if (directory != null) { start.ArgumentList.Add("-C"); start.ArgumentList.Add(directory); }
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Cannot start git.");
        using var registration = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line) yield return line;
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new GitException($"git {args[0]} failed: {await errors}", process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }
    private static async Task<string> Command(string? directory, string[] args, CancellationToken token)
    {
        var lines = new List<string>();
        await foreach (var line in Run(directory, args, token)) lines.Add(line);
        return string.Join('\n', lines);
    }
    public async Task<string> Head(Source source, CancellationToken token)
    {
        return (await Command(Repo(source), ["rev-parse", "HEAD^{commit}"], token)).Trim();
    }
    public async Task Retain(Source source, string commit, CancellationToken token, bool pending = false) =>
        await Command(Repo(source), ["update-ref", (pending ? "refs/pending/" : "refs/checkpoints/") +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id)))[..16], commit], token);

    public async Task<bool> HasCommit(Source source, string commit, CancellationToken token)
    {
        try { await Command(Repo(source), ["cat-file", "-e", $"{commit}^{{tree}}"], token); return true; }
        catch (GitException error) when (error.ExitCode is 1 or 128)
        {
            Console.WriteLine($"Source cache lacks prior tree for {source.Id}: {commit}. Full reconciliation required.");
            return false;
        }
    }

    public async Task<string> Resolve(Source source, string commit, CancellationToken token)
    {
        if (commit.Length is not (40 or 64) || !commit.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Rollback must specify a full hexadecimal commit ID.");
        return (await Command(Repo(source), ["rev-parse", "--verify", $"{commit}^{{commit}}"], token)).Trim();
    }

    public async Task<HashSet<string>> Read(Source source, string commit, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in Run(Repo(source), ["show", $"{commit}:{source.Path}"], token))
        {
            ParsedLines++;
            var key = Entries.Parse(line, source.IsIp);
            if (key != null && !result.Add(key))
                throw new InvalidDataException($"Feed {source.Id} contains duplicate canonical entry {key}; choose a unique-entry feed.");
        }
        if (result.Count == 0) throw new IOException($"Refusing empty feed {source.Id}.");
        return result;
    }
    public async Task<List<Change>> Diff(ListSnapshot old, string commit, CancellationToken token)
    {
        var source = old.Source;
        await Command(Repo(source), ["cat-file", "-e", $"{old.Commit}^{{tree}}"], token);
        var delta = new Dictionary<string, int>();
        var hunk = false;
        await foreach (var line in Run(Repo(source), ["diff", "--no-ext-diff", "--no-textconv",
            "--no-renames", "--no-color", "--unified=0", "--text", old.Commit, commit, "--", source.Path], token))
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal)) { hunk = false; continue; }
            if (line.StartsWith("@@ ", StringComparison.Ordinal)) { hunk = true; continue; }
            if (!hunk || line.Length == 0 || line[0] is not ('+' or '-')) continue;
            ParsedLines++;
            var key = Entries.Parse(line[1..], source.IsIp);
            if (key != null) delta[key] = delta.GetValueOrDefault(key) + (line[0] == '+' ? 1 : -1);
        }
        var changes = new List<Change>();
        foreach (var (key, amount) in delta)
        {
            if (amount == 0) continue;
            if (Math.Abs(amount) != 1) throw new InvalidDataException($"Feed {source.Id} violates the unique-entry contract for {key}.");
            changes.Add(new(key, source.Id, amount > 0));
        }
        return changes;
    }

}
