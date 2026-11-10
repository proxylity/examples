namespace Iperf2.MicroVm;

/// <summary>Identifies one iperf2 UDP flow: who sent it and which listener port it arrived on.</summary>
public readonly record struct FlowKey(string RemoteAddress, int RemotePort, int LocalPort)
{
    public override string ToString() => $"{RemoteAddress}:{RemotePort}>{LocalPort}";
}

/// <summary>A data datagram, reduced to what the statistics need. Times are Unix microseconds.</summary>
public readonly record struct Arrival(long Sequence, int Length, long ReceivedMicroseconds, long SentMicroseconds);

/// <summary>A final (FIN) datagram.</summary>
public readonly record struct FinArrival(long Sequence, long ReceivedMicroseconds, bool Uses64BitSequence, int Length = 0);

/// <summary>Running counters for a flow, used for logging.</summary>
public readonly record struct FlowCounters(
    long Received, long Bytes, long HighestSequence, long OutOfOrder, long Duplicates, long JitterMicroseconds, int Pending);

public enum FinAction
{
    /// <summary>No flow is known for this FIN (it never sent data, or was already evicted).</summary>
    Unknown,
    /// <summary>First FIN, or a retry inside the grace period. No reply yet, so the client sends another.</summary>
    Withheld,
    /// <summary>The test is finished and the report was just calculated.</summary>
    Final,
    /// <summary>A retry for a test that was already finished; the cached report is sent again.</summary>
    Replay,
}

public readonly record struct FinOutcome(FinAction Action, SessionReport Report, long Duplicates, bool Uses64BitSequence);

public sealed class FlowTableOptions
{
    /// <summary>How long after the first FIN the report is held back, so that late data can still be counted.</summary>
    public TimeSpan FinalizationGrace { get; set; } = TimeSpan.FromMilliseconds(150);
    /// <summary>A flow with no packets for this long is dropped (client crashed, FIN lost).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>How long a finished flow's report is kept so repeated FINs get the same answer.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Batches can be processed in a different order than their packets arrived (they are delivered
    /// concurrently). Packets are held this long, by ReceivedAt, before they are folded into the
    /// statistics, so out-of-order and jitter reflect arrival order, not processing order.
    /// </summary>
    public TimeSpan ReorderWindow { get; set; } = TimeSpan.FromMilliseconds(500);
    public int MaxFlows { get; set; } = 10_000;
    /// <summary>Highest sequence number tracked exactly. 2^26 bits is an 8 MB bitmap per flow, at most.</summary>
    public long MaxTrackedSequence { get; set; } = 1L << 26;
}

/// <summary>
/// All state for in-progress iperf2 tests, held in memory. Without a long-lived process this would need an
/// external store and atomic merges; in a MicroVM it's a dictionary, a bitmap per flow, and a lock.
/// </summary>
public sealed class FlowTable(FlowTableOptions options, Func<long>? monotonicMilliseconds = null)
{
    private readonly Func<long> _now = monotonicMilliseconds ?? (() => Environment.TickCount64);
    private readonly Lock _gate = new();
    private readonly Dictionary<FlowKey, Flow> _flows = [];
    private readonly Dictionary<FlowKey, Completed> _completed = [];
    private long _evicted;

    public int ActiveFlows { get { lock (_gate) return _flows.Count; } }
    public int CompletedFlows { get { lock (_gate) return _completed.Count; } }
    public long Evicted { get { lock (_gate) return _evicted; } }

    /// <summary>
    /// Adds data datagrams for one flow. They are buffered and folded into the statistics in
    /// ReceivedAt order once they are older than the reorder window. Returns null when ignored.
    /// </summary>
    public FlowCounters? AddData(FlowKey key, ReadOnlySpan<Arrival> arrivals)
    {
        if (arrivals.IsEmpty) return null;
        lock (_gate)
        {
            var now = _now();
            if (_completed.TryGetValue(key, out var done))
            {
                // Straggler from a test that has already been reported, unless this is a brand new
                // test reusing the same source port (it restarts at sequence 1).
                var restart = false;
                foreach (var arrival in arrivals) if (arrival.Sequence <= 1) { restart = true; break; }
                if (!restart && done.ExpiresAt > now) return null;
                _completed.Remove(key);
            }

            if (!_flows.TryGetValue(key, out var flow))
            {
                if (_flows.Count >= options.MaxFlows) return null;
                flow = new Flow();
                _flows.Add(key, flow);
            }
            flow.LastTouched = now;

            foreach (var arrival in arrivals)
            {
                // The delivery order breaks ties between equal ReceivedAt values (see Fold).
                flow.Pending.Add((arrival, flow.NextOrder++));
                if (arrival.ReceivedMicroseconds > flow.LatestReceived) flow.LatestReceived = arrival.ReceivedMicroseconds;
            }
            Fold(flow, flow.LatestReceived - (long)options.ReorderWindow.TotalMicroseconds);
            return flow.Counters();
        }
    }

    /// <summary>Handles a FIN datagram. See <see cref="FinAction"/> for the possible outcomes.</summary>
    public FinOutcome Fin(FlowKey key, FinArrival fin)
    {
        lock (_gate)
        {
            var now = _now();
            if (_completed.TryGetValue(key, out var done) && done.ExpiresAt > now)
                return new(FinAction.Replay, done.Report, done.Duplicates, fin.Uses64BitSequence);

            if (!_flows.TryGetValue(key, out var flow))
                return new(FinAction.Unknown, default, 0, fin.Uses64BitSequence);

            flow.LastTouched = now;

            // The client resends its FIN about every 10 ms until it hears back, and every resend
            // carries the next sequence number (-n, -(n+1), ...). The first FIN, the one with the
            // smallest magnitude, is the true end of the test: it's datagram n of n.
            if (flow.FinalSequence == 0 || fin.Sequence < flow.FinalSequence) flow.FinalSequence = fin.Sequence;

            if (flow.FirstFinMicroseconds == 0)
            {
                // Like a stock server, count the (first) FIN as a received datagram.
                flow.FirstFinMicroseconds = fin.ReceivedMicroseconds;
                flow.FirstFinSeenAt = now;
                flow.Received++;
                flow.Bytes += fin.Length;
                return new(FinAction.Withheld, default, 0, fin.Uses64BitSequence);
            }
            if (fin.ReceivedMicroseconds < flow.FirstFinMicroseconds) flow.FirstFinMicroseconds = fin.ReceivedMicroseconds;
            // The grace gives batches that are still in flight time to land, so it is measured on this
            // machine's clock (processing time). ReceivedAt (event time) is only used for the statistics.
            if (now - flow.FirstFinSeenAt < (long)options.FinalizationGrace.TotalMilliseconds)
                return new(FinAction.Withheld, default, 0, fin.Uses64BitSequence);

            Fold(flow, long.MaxValue); // the test is over: everything still buffered counts now
            var report = flow.Report();
            _flows.Remove(key);
            _completed[key] = new(report, flow.Duplicates, now + (long)options.CompletedRetention.TotalMilliseconds);
            return new(FinAction.Final, report, flow.Duplicates, fin.Uses64BitSequence);
        }
    }

    /// <summary>Removes idle flows and expired reports. Returns the number of flows evicted.</summary>
    public int Evict()
    {
        lock (_gate)
        {
            var now = _now();
            var idle = (long)options.IdleTimeout.TotalMilliseconds;
            var count = 0;
            foreach (var (key, flow) in _flows.ToArray())
            {
                if (now - flow.LastTouched < idle) continue;
                _flows.Remove(key);
                count++;
            }
            foreach (var (key, done) in _completed.ToArray())
                if (done.ExpiresAt <= now) _completed.Remove(key);
            _evicted += count;
            return count;
        }
    }

    /// <summary>
    /// Folds buffered arrivals received at or before <paramref name="watermark"/>, oldest first. Equal
    /// ReceivedAt values keep the order they were delivered in (List.Sort alone is not stable, and
    /// coarse timestamps make ties common).
    /// </summary>
    private void Fold(Flow flow, long watermark)
    {
        var pending = flow.Pending;
        if (pending.Count == 0) return;
        pending.Sort(static (a, b) =>
        {
            var byTime = a.Arrival.ReceivedMicroseconds.CompareTo(b.Arrival.ReceivedMicroseconds);
            return byTime != 0 ? byTime : a.Order.CompareTo(b.Order);
        });
        var ready = 0;
        while (ready < pending.Count && pending[ready].Arrival.ReceivedMicroseconds <= watermark) ready++;
        for (var i = 0; i < ready; i++) Apply(flow, pending[i].Arrival);
        pending.RemoveRange(0, ready);
    }

    private void Apply(Flow flow, in Arrival arrival)
    {
        if (arrival.Sequence < 0) return;
        if (arrival.Sequence >= options.MaxTrackedSequence) { flow.Rejected++; return; }

        if (!flow.MarkSeen(arrival.Sequence))
        {
            flow.Duplicates++;
            return;
        }

        flow.Received++;
        flow.Bytes += arrival.Length;
        if (arrival.Sequence < flow.HighestSequence) flow.OutOfOrder++;
        else flow.HighestSequence = arrival.Sequence;
        if (flow.FirstMicroseconds == 0 || arrival.ReceivedMicroseconds < flow.FirstMicroseconds)
            flow.FirstMicroseconds = arrival.ReceivedMicroseconds;

        // Jitter is the smoothed difference between consecutive transit times (RFC 1889), the same
        // estimator iperf2 uses: J += (|D| - J) / 16. Transit uses the gateway's receive time.
        var transit = arrival.ReceivedMicroseconds - arrival.SentMicroseconds;
        if (flow.HasTransit) flow.Jitter += (Math.Abs(transit - flow.PreviousTransit) - flow.Jitter) / 16;
        flow.PreviousTransit = transit;
        flow.HasTransit = true;
    }

    private readonly record struct Completed(SessionReport Report, long Duplicates, long ExpiresAt);

    private sealed class Flow
    {
        private ulong[] _seen = new ulong[16];

        public readonly List<(Arrival Arrival, long Order)> Pending = [];
        public long LatestReceived, NextOrder, FirstFinSeenAt;

        public long HighestSequence = -1;
        public long Received, Bytes, OutOfOrder, Duplicates, Rejected;
        public long FirstMicroseconds, FirstFinMicroseconds, FinalSequence;
        public long PreviousTransit;
        public bool HasTransit;
        public double Jitter;
        public long LastTouched;

        /// <summary>Sets the bit for a sequence number. Returns false if it was already set (a duplicate).</summary>
        public bool MarkSeen(long sequence)
        {
            var word = (int)(sequence >> 6);
            if (word >= _seen.Length) Array.Resize(ref _seen, Math.Max(word + 1, _seen.Length * 2));
            var mask = 1UL << (int)(sequence & 63);
            if ((_seen[word] & mask) != 0) return false;
            _seen[word] |= mask;
            return true;
        }

        /// <summary>Counters for logging. Buffered datagrams are not folded yet; they are reported as Pending.</summary>
        public FlowCounters Counters() => new(Received, Bytes, HighestSequence, OutOfOrder, Duplicates, (long)Jitter, Pending.Count);

        public SessionReport Report()
        {
            // The first FIN's sequence is the number of datagrams the client sent (data plus that
            // FIN), so loss is that count minus the unique datagrams actually received. Nothing is
            // "lost" until the test is over, so a late datagram that fills a gap is never counted.
            var sent = Math.Max(FinalSequence, HighestSequence + 1);
            var duration = FirstFinMicroseconds > FirstMicroseconds ? FirstFinMicroseconds - FirstMicroseconds : 0;
            return new(Bytes, duration, Math.Max(0, sent - Received), OutOfOrder, sent, (long)Jitter);
        }
    }
}
