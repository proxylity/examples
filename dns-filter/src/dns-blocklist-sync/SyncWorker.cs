namespace DnsBlocklistSync;

public sealed class SyncWorker(IStore store, GitSource git, string directory, int concurrency = 16)
{
    public long Added;
    public long Removed;
    public long ProtectedConflicts;

    public async Task Run(string profile, Source[] sources, CancellationToken cancellationToken,
        string? rollbackSource = null, string? rollbackCommit = null)
    {
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "initialized-v2");
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await store.Acquire(work.Token);
        var renewal = Renew(work);
        DurableState? state = null;
        Snapshot? pending = null;
        try
        {
            state = await store.ReadState(work.Token);
            pending = state?.Pending;
            var previous = state?.Checkpoint;
            var full = !File.Exists(marker) || previous?.Schema != 2;
            if (!full)
                foreach (var old in previous!.Lists.Values.Where(old => sources.Any(source => source == old.Source)))
                    if (!await git.HasCommit(old.Source, old.Commit, work.Token)) full = true;
            if (!full && state?.Pending is { } priorTarget)
                foreach (var list in priorTarget.Lists.Values)
                    if (!await git.HasCommit(list.Source, list.Commit, work.Token)) full = true;
            var target = new Snapshot { Schema = 2, Profile = profile };
            foreach (var source in sources)
            {
                var head = await git.Head(source, work.Token);
                if (source.Id == rollbackSource) head = await git.Resolve(source, rollbackCommit!, work.Token);
                target.Lists[source.Id] = new() { Source = source, Commit = head };
            }
            if (!full && state?.Pending is { } interrupted) target = interrupted;
            List<Change> changes = [];
            if (full)
            {
                Console.WriteLine("Source cache initialization: full managed membership reconciliation.");
                var desired = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var (id, list) in target.Lists)
                    foreach (var key in await git.Read(list.Source, list.Commit, work.Token))
                    {
                        if (!desired.TryGetValue(key, out var ids)) desired[key] = ids = [];
                        ids.Add(id);
                    }
                await foreach (var record in store.ReadManaged(work.Token))
                {
                    desired.TryGetValue(record.Key, out var wanted);
                    foreach (var id in record.Sources)
                        if (wanted?.Remove(id) != true) changes.Add(new(record.Key, id, false));
                    if (record.Sources.Count == 0) await store.Cleanup(record.Key, work.Token);
                }
                foreach (var (key, ids) in desired)
                    foreach (var id in ids) changes.Add(new(key, id, true));
            }
            else
            {
                foreach (var (id, list) in target.Lists)
                {
                    if (previous!.Lists.TryGetValue(id, out var old) && old.Source == list.Source)
                    {
                        if (old.Commit != list.Commit) changes.AddRange(await git.Diff(old, list.Commit, work.Token));
                    }
                    else
                        changes.AddRange((await git.Read(list.Source, list.Commit, work.Token)).Select(key => new Change(key, id, true)));
                }
                var disabled = previous!.Lists.Keys.Except(target.Lists.Keys).ToHashSet();
                if (disabled.Count > 0)
                    await foreach (var record in store.ReadManaged(work.Token))
                        foreach (var id in record.Sources.Where(disabled.Contains))
                            changes.Add(new(record.Key, id, false));
            }
            // Keep only commit/configuration metadata. Repeating a delta after interrupted writes
            // is safe because membership ADD/DELETE is idempotent; no entry snapshot or journal.
            foreach (var list in target.Lists.Values) await git.Retain(list.Source, list.Commit, work.Token, pending: true);
            pending = target;
            await store.SaveState(new("running", previous, state?.LastSuccess ?? 0, Pending: target), work.Token);
            foreach (var adding in new[] { true, false })
                await Parallel.ForEachAsync(changes.Where(x => x.Add == adding),
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = work.Token },
                    async (change, token) =>
                    {
                        var result = await store.Apply(change, token);
                        if (result == WriteResult.Protected) Interlocked.Increment(ref ProtectedConflicts);
                        else if (result == WriteResult.Applied)
                        {
                            if (adding) Interlocked.Increment(ref Added); else Interlocked.Increment(ref Removed);
                        }
                    });
            await store.SaveState(new("success", target, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), work.Token);
            foreach (var list in target.Lists.Values) await git.Retain(list.Source, list.Commit, work.Token);
            File.WriteAllText(marker, "2");
        }
        catch (Exception error)
        {
            try { await store.SaveState(new("failed", state?.Checkpoint, state?.LastSuccess ?? 0,
                error.Message[..Math.Min(error.Message.Length, 1000)], pending), work.Token); }
            catch (Exception statusError) { Console.Error.WriteLine($"Cannot persist failure: {statusError.Message}"); }
            throw;
        }
        finally
        {
            work.Cancel();
            await renewal;
            await store.Release(CancellationToken.None);
        }
    }

    private async Task Renew(CancellationTokenSource work)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), work.Token);
                await store.Renew(work.Token);
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine($"Lease renewal failed: {error.Message}"); work.Cancel(); }
    }
}
