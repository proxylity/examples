using System.Buffers.Binary;
using System.Text.Json.Serialization.Metadata;
using Iperf2.MicroVm;
using Xunit;

namespace Iperf2.Tests;

public sealed class BatchProcessorTests
{
    private sealed class ListLog : IEventLog
    {
        public List<object> Events { get; } = [];
        public void Write<T>(T value, JsonTypeInfo<T> typeInfo) => Events.Add(value!);
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly ListLog _log = new();
    private readonly BatchProcessor _processor;
    private long _clock;

    public BatchProcessorTests()
    {
        var table = new FlowTable(new() { ReorderWindow = TimeSpan.Zero }, () => _clock);
        _processor = new BatchProcessor(table, _log, () => new AppSettings(null, null, null), () => "mvm-test");
    }

    private static string Datagram(long sequence, int length = 1470)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1_700_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), sequence < 0 ? uint.MaxValue : 0u);
        if (sequence is 1 or 2) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 0x48010098);
        return Convert.ToBase64String(bytes);
    }

    private static RequestPacket Packet(string tag, int remotePort, string data, int offsetMilliseconds, string? formatter = "base64")
        => new(tag, new("203.0.113.10", remotePort), new("ingress-1", 15678), formatter, data, Start.AddMilliseconds(offsetMilliseconds).ToString("O"));

    [Fact]
    public void ProducesReportOnlyOnTheFinThatEndsTheGrace()
    {
        var data = Enumerable.Range(1, 20).Select(i => Packet($"d{i}", 40000, Datagram(i), i * 10)).ToArray();
        Assert.Empty(_processor.Process(new(data)).Replies);

        Assert.Empty(_processor.Process(new([Packet("fin1", 40000, Datagram(-21), 400)])).Replies);
        _clock = 200; // 200 ms later on the server's clock: past the 150 ms grace
        // Retries arrive every 10 ms; several can share a batch, and only one reply is sent.
        var response = _processor.Process(new([
            Packet("fin2", 40000, Datagram(-22), 560), Packet("fin3", 40000, Datagram(-23), 570), Packet("fin4", 40000, Datagram(-24), 580)]));

        var reply = Assert.Single(response.Replies);
        Assert.Equal("fin2", reply.Tag);
        var report = Convert.FromBase64String(reply.Data);
        Assert.Equal(128, report.Length);
        Assert.Equal(21u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(44))); // datagrams: 20 data + the FIN
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(36)));  // lost
        Assert.Equal(21u * 1470, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(24))); // bytes
    }

    [Fact]
    public void OneBatchCanCarryManyFlows()
    {
        var packets = new List<RequestPacket>();
        for (var i = 1; i <= 10; i++)
        {
            packets.Add(Packet($"a{i}", 40000, Datagram(i), i * 10));
            packets.Add(Packet($"b{i}", 40001, Datagram(i), i * 10 + 1));
        }
        packets.RemoveAll(p => p.Tag == "b5"); // flow b loses one datagram
        _processor.Process(new([.. packets]));
        _processor.Process(new([Packet("fa1", 40000, Datagram(-11), 300), Packet("fb1", 40001, Datagram(-11), 300)]));
        _clock = 200;
        var response = _processor.Process(new([Packet("fa2", 40000, Datagram(-12), 600), Packet("fb2", 40001, Datagram(-12), 600)]));

        var lostByTag = response.Replies.ToDictionary(r => r.Tag, r => BinaryPrimitives.ReadUInt32BigEndian(Convert.FromBase64String(r.Data).AsSpan(36)));
        Assert.Equal(0u, lostByTag["fa2"]);
        Assert.Equal(1u, lostByTag["fb2"]);
    }

    [Fact]
    public void BadPacketsAreDroppedWithoutFailingTheBatch()
    {
        var good = Packet("good", 40000, Datagram(1), 10);
        var batch = new GatewayRequest([
            Packet("notbase64", 40000, "***not base64***", 5),
            Packet("tiny", 40000, Convert.ToBase64String(new byte[4]), 6),
            Packet("wrongformat", 40000, Datagram(2), 7, "hex"),
            good,
        ]);

        var response = _processor.Process(batch);

        Assert.Empty(response.Replies);
        var logged = Assert.IsType<BatchEvent>(_log.Events.Last());
        Assert.Equal(3, logged.Dropped);
        var flow = Assert.Single(logged.Flows);
        Assert.Equal(1, flow.Received);
    }

    [Fact]
    public void FlagsWholeSecondTimestamps()
    {
        var coarse = new RequestPacket("c", new("203.0.113.10", 40000), new("ingress-1", 15678), "base64", Datagram(1), "2026-11-09T11:59:59");
        _processor.Process(new([coarse, Packet("fine", 40000, Datagram(2), 123)]));
        var logged = Assert.IsType<BatchEvent>(_log.Events.Last());
        Assert.Equal(1, logged.WholeSecondTimestamps);
        Assert.Equal("2026-11-09T11:59:59", logged.FirstReceivedAt);
    }

    [Fact]
    public void EmptyBatchProducesEmptyReplies()
    {
        Assert.Empty(_processor.Process(new([])).Replies);
        Assert.Empty(_processor.Process(new(null)).Replies);
    }

    [Fact]
    public void DatagramLengthIsTakenFromTheBase64LengthWithoutFullDecode()
    {
        // 1470 bytes encodes with no padding; 1471 and 1472 bytes use padding.
        foreach (var length in new[] { 1470, 1471, 1472 })
        {
            var log = new ListLog();
            var processor = new BatchProcessor(new FlowTable(new() { ReorderWindow = TimeSpan.Zero }, () => 0), log, () => new(null, null, null), () => "m");
            processor.Process(new([Packet("p", 40000, Datagram(1, length), 10)]));
            var flow = Assert.Single(Assert.IsType<BatchEvent>(log.Events.Last()).Flows);
            Assert.Equal(length, flow.Bytes);
        }
    }
}
