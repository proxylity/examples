using System.Text.Json;
using Iperf2.MicroVm;
using Xunit;

namespace Iperf2.Tests;

public sealed class EventLogTests
{
    private static FinEvent Fin(long seq) => new("fin", "m", "f", seq, "withheld");
    private static StatsEvent Stats() => new("stats", "m", 0, 0, 0, 0, 0);

    [Fact]
    public void WritesLinesAndReadsBackAfterACursor()
    {
        var output = new StringWriter();
        var log = new EventLog(output: output);
        log.Write(Fin(1), AppJson.Default.FinEvent);
        var cursor = log.Next;
        log.Write(Fin(2), AppJson.Default.FinEvent);
        log.Write(Stats(), AppJson.Default.StatsEvent);

        Assert.Equal(3, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using var doc = JsonDocument.Parse(log.Read(cursor, null));
        Assert.Equal(4, doc.RootElement.GetProperty("Next").GetInt64());
        var events = doc.RootElement.GetProperty("Events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal(2, events[0].GetProperty("FinSequence").GetInt64());
    }

    [Fact]
    public void FiltersByEventType()
    {
        var log = new EventLog(output: TextWriter.Null);
        log.Write(Fin(1), AppJson.Default.FinEvent);
        log.Write(Stats(), AppJson.Default.StatsEvent);
        using var doc = JsonDocument.Parse(log.Read(0, "stats"));
        var only = Assert.Single(doc.RootElement.GetProperty("Events").EnumerateArray());
        Assert.Equal("stats", only.GetProperty("Event").GetString());
    }

    [Fact]
    public void KeepsOnlyTheMostRecentEvents()
    {
        var log = new EventLog(capacity: 3, output: TextWriter.Null);
        for (var i = 1; i <= 5; i++) log.Write(Fin(i), AppJson.Default.FinEvent);
        using var doc = JsonDocument.Parse(log.Read(0, null));
        var seqs = doc.RootElement.GetProperty("Events").EnumerateArray().Select(e => e.GetProperty("FinSequence").GetInt64());
        Assert.Equal([3L, 4L, 5L], seqs);
    }
}
