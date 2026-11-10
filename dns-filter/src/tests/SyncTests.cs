using System.Diagnostics;
using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DnsBlocklistSync;
using Moq;
using Xunit;

namespace test;

public sealed class SyncTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "dns-sync-test-" + Guid.NewGuid().ToString("N"));
    readonly string repository;
    readonly string cache;
    public SyncTests()
    {
        repository = Path.Combine(directory, "source");
        cache = Path.Combine(directory, "cache");
        Directory.CreateDirectory(repository);
        Git("init", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
    }
    string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
    void Commit(string text)
    {
        File.WriteAllText(Path.Combine(repository, "list.txt"), text);
        Git("add", ".");
        Git("commit", "-m", "fixture");
    }
    Source Source(string id = "test") => new(id, repository, "list.txt");

    [Fact]
    public async Task GitParsesOnlyDeltaAndHandlesReorderAndForcePush()
    {
        Commit("# header\nONE.example\nsame.example\nremoved.example\n");
        var git = new GitSource();
        var head = await git.Head(Source(), default);
        var old = await git.Read(Source(), head, default);
        await git.Retain(Source(), head, default);
        var initial = git.ParsedLines;
        Commit("same.example\n# header\none.example\nadded.example\n");
        var nextCommit = await git.Head(Source(), default);
        var next = await git.Diff(new() { Source = Source(), Commit = head }, nextCommit, default);
        Assert.Equal(2, next.Count);
        Assert.Contains(next, x => x.Key == "added.example" && x.Add);
        Assert.Contains(next, x => x.Key == "removed.example" && !x.Add);
        Assert.True(git.ParsedLines > initial);
        Git("checkout", "--orphan", "replacement");
        Commit("added.example\nfresh.example\n");
        Git("branch", "-M", "main");
        var rewritten = await git.Diff(new() { Source = Source(), Commit = nextCommit }, await git.Head(Source(), default), default);
        Assert.Contains(rewritten, x => x.Key == "fresh.example" && x.Add);
        Assert.DoesNotContain(rewritten, x => x.Key == "added.example");
    }

    [Fact]
    public async Task LargeFileUpdateParsesOnlyTwoChangedLinesAndStoresNoEntries()
    {
        var lines = Enumerable.Range(0, 5000).Select(i => $"domain{i}.example").ToArray();
        Commit(string.Join('\n', lines) + "\n");
        var store = new Store();
        var git = new GitSource();
        await new SyncWorker(store, git, cache).Run("Balanced", [Source()], default);
        var baseline = git.ParsedLines;
        lines[2500] = "replacement.example";
        Commit(string.Join('\n', lines) + "\n");
        store.Changes.Clear();
        await new SyncWorker(store, git, cache).Run("Balanced", [Source()], default);
        Assert.Equal(2, git.ParsedLines - baseline);
        Assert.Equal(2, store.Changes.Count);
        var metadata = System.Text.Json.JsonSerializer.Serialize(store.State);
        Assert.DoesNotContain("domain0.example", metadata);
        Assert.DoesNotContain("replacement.example", metadata);
        Assert.True(metadata.Length < 2000);
    }

    [Fact]
    public async Task CachedInterruptedDeltaCompletesBeforeNewerUpstreamHead()
    {
        Commit("one.example\ntwo.example\n");
        var store = new Store();
        await new SyncWorker(store, new GitSource(), cache, 1).Run("Balanced", [Source()], default);
        Commit("one.example\ntemporary.example\nfailure.example\n");
        store.FailKey = "failure.example";
        await Assert.ThrowsAsync<IOException>(() => new SyncWorker(store, new GitSource(), cache, 1)
            .Run("Balanced", [Source()], default));
        Commit("one.example\nlatest.example\n");
        store.FailKey = null;
        await new SyncWorker(store, new GitSource(), cache, 1).Run("Balanced", [Source()], default);
        Assert.Contains("temporary.example", store.Records.Keys);
        Assert.Null(store.State!.Pending);
        await new SyncWorker(store, new GitSource(), cache, 1).Run("Balanced", [Source()], default);
        Assert.Equal(new[] { "latest.example", "one.example" }, store.Records.Keys.Order().ToArray());
    }

    [Fact]
    public async Task DuplicateBootstrapIsExplicitlyRejected()
    {
        Commit("one.example\nONE.example\n");
        var git = new GitSource();
        await Assert.ThrowsAsync<InvalidDataException>(() => git.Read(Source(), Git("rev-parse", "HEAD"), default));
    }

    [Fact]
    public async Task CachedSyncUnrelatedCommitMakesNoFeedWritesOrParsing()
    {
        Commit("one.example\ntwo.example\n");
        var store = new Store();
        var git = new GitSource();
        await new SyncWorker(store, git, cache).Run("Balanced", [Source()], default);
        var parsed = git.ParsedLines;
        store.Changes.Clear();
        File.WriteAllText(Path.Combine(repository, "README"), "unrelated");
        Git("add", "."); Git("commit", "-m", "unrelated");
        await new SyncWorker(store, git, cache).Run("Balanced", [Source()], default);
        Assert.Empty(store.Changes);
        Assert.Equal(parsed, git.ParsedLines);
        await new SyncWorker(store, git, cache).Run("Balanced", [Source()], default);
        Assert.Empty(store.Changes);
        Assert.Equal(parsed, git.ParsedLines);
    }

    [Fact]
    public async Task LostCacheReconcilesStaleEntriesWithoutWritingUnchangedMembership()
    {
        Commit("one.example\ntwo.example\n");
        var store = new Store();
        await new SyncWorker(store, new GitSource(), cache).Run("Balanced", [Source()], default);
        store.Changes.Clear();
        File.Delete(Path.Combine(cache, "initialized-v2"));
        Commit("one.example\nnew.example\n");
        await new SyncWorker(store, new GitSource(), cache).Run("Balanced", [Source()], default);
        Assert.DoesNotContain(store.Changes, x => x.Key == "one.example");
        Assert.Contains(store.Changes, x => x.Key == "two.example" && !x.Add);
        Assert.Contains(store.Changes, x => x.Key == "new.example" && x.Add);
    }

    [Fact]
    public async Task InterruptedWritesReconcileAndPreserveOverlappingSources()
    {
        Commit("one.example\ntwo.example\n");
        var store = new Store { FailKey = "two.example" };
        await Assert.ThrowsAsync<IOException>(() => new SyncWorker(store, new GitSource(), cache, 1)
            .Run("Balanced", [Source("a"), Source("b")], default));
        Assert.NotNull(store.State!.Pending);
        store.FailKey = null;
        await new SyncWorker(store, new GitSource(), cache).Run("Balanced", [Source("a"), Source("b")], default);
        Assert.Equal(2, store.Records["one.example"].Count);
        await new SyncWorker(store, new GitSource(), cache).Run("Balanced", [Source("b")], default);
        Assert.Equal(new[] { "b" }, store.Records["one.example"].ToArray());
        Assert.Equal("success", store.State!.Status);
    }

    [Fact]
    public async Task DynamoWritesAreOwnerGuarded()
    {
        var db = new Mock<IAmazonDynamoDB>();
        UpdateItemRequest? actual = null;
        db.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((request, _) => actual = request)
            .ReturnsAsync(new UpdateItemResponse());
        var store = new DynamoStore(db.Object, "table");
        await store.Apply(new("example.com", "feed", true), default);
        Assert.Contains("attribute_not_exists(PK) OR", actual!.ConditionExpression);
        Assert.Equal("blocklist-sync", actual.ExpressionAttributeValues[":sync"].S);
        db.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("protected"));
        db.Setup(x => x.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse { Item = new() { ["PK"] = new() { S = "example.com" } } });
        Assert.Equal(WriteResult.Protected, await store.Apply(new("example.com", "feed", true), default));
    }

    [Theory]
    [InlineData("*.EXAMPLE.com.", false, "example.com")]
    [InlineData("192.0.2.129/24", true, "IP#192.0.2.0/24")]
    [InlineData("2001:db8::abcd/64", true, "IP#2001:db8::/64")]
    public void CanonicalEntryParsing(string text, bool ip, string expected) => Assert.Equal(expected, Entries.Parse(text, ip));

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }

    sealed class Store : IStore
    {
        public Dictionary<string, HashSet<string>> Records = [];
        public List<Change> Changes = [];
        public DurableState? State;
        public string? FailKey;
        public Task Acquire(CancellationToken token) => Task.CompletedTask;
        public Task Renew(CancellationToken token) => Task.CompletedTask;
        public Task Release(CancellationToken token) => Task.CompletedTask;
        public Task<DurableState?> ReadState(CancellationToken token) => Task.FromResult(State);
        public Task SaveState(DurableState state, CancellationToken token) { State = state; return Task.CompletedTask; }
        public async IAsyncEnumerable<ManagedRecord> ReadManaged([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var (key, sources) in Records) { yield return new(key, new(sources)); await Task.Yield(); }
        }
        public Task Cleanup(string key, CancellationToken token) { Records.Remove(key); return Task.CompletedTask; }
        public Task<WriteResult> Apply(Change change, CancellationToken token)
        {
            lock (Records)
            {
                if (change.Key == FailKey) throw new IOException("injected failure");
                Changes.Add(change);
                if (change.Add)
                {
                    if (!Records.TryGetValue(change.Key, out var sources)) Records[change.Key] = sources = [];
                    sources.Add(change.Source);
                }
                else if (Records.TryGetValue(change.Key, out var sources))
                {
                    sources.Remove(change.Source);
                    if (sources.Count == 0) Records.Remove(change.Key);
                }
            }
            return Task.FromResult(WriteResult.Applied);
        }
    }
}
