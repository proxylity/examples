using Iperf2.MicroVm;
using Xunit;

namespace Iperf2.Tests;

/// <summary>
/// The statistics rules, checked against what a stock iperf2 2.1.9 server reports. In a stock
/// capture, a client sending data 1..181 followed by FIN -182 gets "0/182 (0%)" and a byte total of
/// 182 x 1470: the first FIN counts as datagram n of n. FIN retries carry -183, -184, ... and are ignored.
/// </summary>
public sealed class FlowTableTests
{
    private static readonly FlowKey Flow = new("203.0.113.10", 40000, 15678);
    private const long T0 = 1_700_000_000_000_000;
    private const int Size = 1470;

    private long _clock;
    private FlowTable NewTable(FlowTableOptions? options = null) => new(options ?? new(), () => _clock);

    private static Arrival Data(long sequence, long atMicroseconds, int length = Size)
        => new(sequence, length, T0 + atMicroseconds, T0 + atMicroseconds - 500);

    private static FinArrival Fin(long sequence, long atMicroseconds) => new(sequence, T0 + atMicroseconds, true, Size);

    /// <summary>
    /// Ends a test whose last data datagram was <paramref name="lastData"/>, the way a real client does:
    /// FIN n+1, then retries n+2, n+3, ... every 10 ms until the report comes back.
    /// </summary>
    private FinOutcome Finish(FlowTable table, long lastData, long atMicroseconds, FlowKey? key = null)
    {
        var flow = key ?? Flow;
        for (var retry = 0; ; retry++)
        {
            var outcome = table.Fin(flow, Fin(lastData + 1 + retry, atMicroseconds + retry * 10_000L));
            _clock += 10; // the grace runs on the server's own clock
            if (outcome.Action != FinAction.Withheld) return outcome;
            Assert.True(retry < 100, "the report was never released");
        }
    }

    [Fact]
    public void CleanTestReportsNoLossAndCountsTheFin()
    {
        var table = NewTable();
        table.AddData(Flow, Enumerable.Range(1, 181).Select(i => Data(i, i * 11_000L)).ToArray());
        var outcome = Finish(table, 181, 2_000_000);

        Assert.Equal(FinAction.Final, outcome.Action);
        Assert.Equal(182, outcome.Report.Datagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
        Assert.Equal(0, outcome.Report.OutOfOrderDatagrams);
        Assert.Equal(182 * Size, outcome.Report.ReceivedBytes); // 267,540 bytes, as the stock server says
        Assert.Equal(2_000_000 - 11_000, outcome.Report.DurationMicroseconds);
        Assert.Equal(0, table.ActiveFlows);
    }

    [Fact]
    public void ReportIsReleasedAfterTheGraceAndUsesTheFirstFin()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        _clock = 30; Assert.Equal(FinAction.Withheld, table.Fin(Flow, Fin(3, 30_000)).Action);
        _clock = 40; Assert.Equal(FinAction.Withheld, table.Fin(Flow, Fin(4, 40_000)).Action);
        _clock = 170; Assert.Equal(FinAction.Withheld, table.Fin(Flow, Fin(5, 170_000)).Action); // 140 ms, grace is 150 ms
        _clock = 190;
        var outcome = table.Fin(Flow, Fin(6, 190_000));

        Assert.Equal(FinAction.Final, outcome.Action);
        Assert.Equal(3, outcome.Report.Datagrams); // from FIN -3, not the retry -6
        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void LostFirstFinOnlyOverstatesTotalByTheRetriesMissed()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        // FIN -3 was lost; -4 arrives first, then -3 never shows. Stock servers have the same limitation.
        table.Fin(Flow, Fin(4, 40_000));
        _clock = 300;
        var outcome = table.Fin(Flow, Fin(5, 300_000));

        Assert.Equal(4, outcome.Report.Datagrams);
        Assert.Equal(1, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void ReorderedFinsStillUseTheSmallest()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        table.Fin(Flow, Fin(4, 40_000)); // a retry overtakes the first FIN
        table.Fin(Flow, Fin(3, 41_000));
        _clock = 300;
        var outcome = table.Fin(Flow, Fin(5, 300_000));

        Assert.Equal(3, outcome.Report.Datagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void GapsAreLossAndLateArrivalsAreNot()
    {
        var table = NewTable();
        table.AddData(Flow, Enumerable.Range(1, 100).Where(i => i != 10 && i != 20).Select(i => Data(i, i * 10_000L)).ToArray());
        table.AddData(Flow, [Data(20, 1_005_000)]); // 20 turns up in a later batch
        var outcome = Finish(table, 100, 1_010_000);

        Assert.Equal(101, outcome.Report.Datagrams);
        Assert.Equal(1, outcome.Report.LostDatagrams); // only 10 never arrived
        Assert.Equal(1, outcome.Report.OutOfOrderDatagrams);
    }

    [Fact]
    public void BatchesProcessedOutOfOrderAreNotOutOfOrderPackets()
    {
        // Two batches delivered concurrently: the later one (by ReceivedAt) is processed first.
        var table = NewTable();
        table.AddData(Flow, [Data(11, 110_000), Data(12, 120_000)]);
        table.AddData(Flow, Enumerable.Range(1, 10).Select(i => Data(i, i * 10_000L)).ToArray());
        var outcome = Finish(table, 12, 200_000);

        Assert.Equal(0, outcome.Report.OutOfOrderDatagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void PacketsOlderThanTheWindowAreFoldedAndCounted()
    {
        var table = NewTable(new() { ReorderWindow = TimeSpan.FromMilliseconds(100) });
        var counters = table.AddData(Flow, [Data(1, 0), Data(2, 50_000), Data(3, 200_000)]);
        Assert.Equal(2, counters!.Value.Received); // 1 and 2 are older than 200 ms - 100 ms; 3 is buffered
        Assert.Equal(1, counters.Value.Pending);
    }

    [Fact]
    public void ReorderedOnTheWireIsStillOutOfOrder()
    {
        var table = NewTable();
        // Packet 3 really did arrive before 2 (its ReceivedAt is earlier), across batches.
        table.AddData(Flow, [Data(1, 10_000), Data(3, 20_000)]);
        table.AddData(Flow, [Data(2, 30_000)]);
        Assert.Equal(1, Finish(table, 3, 100_000).Report.OutOfOrderDatagrams);
    }

    [Fact]
    public void EqualTimestampsKeepDeliveryOrder()
    {
        // With whole-second ReceivedAt values every packet in a second ties. Ties must keep the order the
        // packets were delivered in, or a perfectly ordered test reports reordering.
        var table = NewTable();
        foreach (var chunk in Enumerable.Range(1, 40).Chunk(5))
            table.AddData(Flow, chunk.Select(i => new Arrival(i, Size, T0, T0 - 500)).ToArray());
        var outcome = Finish(table, 40, 0);

        Assert.Equal(0, outcome.Report.OutOfOrderDatagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void GraceUsesTheServerClockNotReceivedAt()
    {
        // Every FIN carries the same (coarse) ReceivedAt; the report must still be released once the
        // grace has passed on the server's clock.
        var table = NewTable();
        table.AddData(Flow, [Data(1, 0), Data(2, 0)]);
        _clock = 1000; Assert.Equal(FinAction.Withheld, table.Fin(Flow, Fin(3, 0)).Action);
        _clock = 1100; Assert.Equal(FinAction.Withheld, table.Fin(Flow, Fin(4, 0)).Action);
        _clock = 1160; Assert.Equal(FinAction.Final, table.Fin(Flow, Fin(5, 0)).Action);
    }

    [Fact]
    public void DuplicatesDoNotInflateCounts()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000), Data(2, 20_500), Data(3, 30_000), Data(1, 30_500)]);
        var outcome = Finish(table, 3, 100_000);

        Assert.Equal(4, outcome.Report.Datagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
        Assert.Equal(4 * Size, outcome.Report.ReceivedBytes);
        Assert.Equal(2, outcome.Duplicates);
    }

    [Fact]
    public void ReorderWithinBatchCountsOutOfOrder()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 1_000), Data(3, 2_000), Data(2, 3_000), Data(4, 4_000)]);
        var outcome = Finish(table, 4, 100_000);

        Assert.Equal(1, outcome.Report.OutOfOrderDatagrams);
        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void JitterIsZeroForSteadyTransitAndPositiveForVariableTransit()
    {
        var steady = NewTable();
        steady.AddData(Flow, Enumerable.Range(1, 50).Select(i => Data(i, i * 10_000L)).ToArray());
        Assert.Equal(0, Finish(steady, 50, 600_000).Report.JitterMicroseconds);

        var table = NewTable();
        // Sent every 10 ms, received with +-1 ms of wobble: transit alternates, |D| = 2 ms.
        var arrivals = Enumerable.Range(1, 50).Select(i =>
            new Arrival(i, 100, T0 + i * 10_000L + (i % 2 == 0 ? 1_000 : -1_000), T0 + i * 10_000L)).ToArray();
        table.AddData(Flow, arrivals);
        Assert.InRange(Finish(table, 50, 600_000).Report.JitterMicroseconds, 1_000, 2_000); // converges toward 2 ms
    }

    [Fact]
    public void DataArrivingDuringGraceIsCounted()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(3, 30_000)]);
        table.Fin(Flow, Fin(4, 40_000));
        table.AddData(Flow, [Data(2, 45_000)]); // a straggler batch lands after the first FIN
        _clock = 300;
        var outcome = table.Fin(Flow, Fin(5, 300_000));

        Assert.Equal(0, outcome.Report.LostDatagrams);
    }

    [Fact]
    public void RepeatedFinAfterCompletionReplaysTheSameReport()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        var final = Finish(table, 2, 100_000);
        var replay = table.Fin(Flow, Fin(40, 600_000));

        Assert.Equal(FinAction.Replay, replay.Action);
        Assert.Equal(final.Report, replay.Report);
    }

    [Fact]
    public void FinForUnknownFlowIsIgnored()
        => Assert.Equal(FinAction.Unknown, NewTable().Fin(Flow, Fin(5, 1_000)).Action);

    [Fact]
    public void StragglerAfterCompletionIsIgnoredButNewTestRestarts()
    {
        var table = NewTable();
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        Finish(table, 2, 100_000);

        Assert.Null(table.AddData(Flow, [Data(2, 700_000)])); // duplicate of old data
        Assert.NotNull(table.AddData(Flow, [Data(1, 800_000)])); // new test reusing the port starts at 1
        Assert.Equal(1, table.ActiveFlows);
    }

    [Fact]
    public void FlowsDoNotContaminateEachOther()
    {
        var table = NewTable();
        var other = Flow with { RemotePort = 40001 };
        table.AddData(Flow, [Data(1, 10_000), Data(2, 20_000)]);
        table.AddData(other, [Data(1, 10_000)]);

        var mine = Finish(table, 2, 100_000);
        var theirs = Finish(table, 1, 100_000, other);

        Assert.Equal(3 * Size, mine.Report.ReceivedBytes);
        Assert.Equal(2 * Size, theirs.Report.ReceivedBytes);
    }

    [Fact]
    public void IdleFlowsAreEvictedAndCompletedReportsExpire()
    {
        var table = NewTable(new() { IdleTimeout = TimeSpan.FromSeconds(60), CompletedRetention = TimeSpan.FromSeconds(10) });
        table.AddData(Flow, [Data(1, 10_000)]);
        _clock = 59_000;
        Assert.Equal(0, table.Evict());
        _clock = 61_000;
        Assert.Equal(1, table.Evict());
        Assert.Equal(0, table.ActiveFlows);
        Assert.Equal(1, table.Evicted);

        table.AddData(Flow, [Data(1, 10_000)]);
        Finish(table, 1, 100_000);
        Assert.Equal(1, table.CompletedFlows);
        _clock += 11_000;
        table.Evict();
        Assert.Equal(0, table.CompletedFlows);
    }

    [Fact]
    public void HugeSequenceNumbersAreRejectedNotAllocated()
    {
        var table = NewTable(new() { MaxTrackedSequence = 1000, ReorderWindow = TimeSpan.Zero });
        var counters = table.AddData(Flow, [Data(1, 10_000), Data(50_000_000_000, 20_000)]);
        Assert.Equal(1, counters!.Value.Received);
    }

    [Fact]
    public void ConcurrentBatchesDoNotLoseUpdates()
    {
        var table = NewTable();
        const int perThread = 5000;
        Parallel.For(0, 8, thread =>
        {
            var arrivals = Enumerable.Range(0, perThread).Select(i => Data(thread * perThread + i + 1, i * 100L)).ToArray();
            foreach (var chunk in arrivals.Chunk(50)) table.AddData(Flow, chunk);
        });
        var outcome = Finish(table, 8 * perThread, 10_000_000);

        Assert.Equal(0, outcome.Report.LostDatagrams);
        Assert.Equal((8L * perThread + 1) * Size, outcome.Report.ReceivedBytes);
    }
}
