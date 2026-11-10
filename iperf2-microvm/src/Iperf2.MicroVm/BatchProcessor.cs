using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Iperf2.MicroVm;

/// <summary>Where structured events go.</summary>
public interface IEventLog
{
    void Write<T>(T value, JsonTypeInfo<T> typeInfo);
}

/// <summary>
/// Writes each event as a JSON line to stdout (which Lambda forwards to CloudWatch Logs, one stream per
/// MicroVM), and also keeps the most recent events in memory so they can be read back from the MicroVM itself
/// (GET /events) immediately, without waiting for log delivery. Each event gets a sequence number so a reader
/// can ask for "everything after N".
/// </summary>
public sealed class EventLog(int capacity = 50_000, TextWriter? output = null) : IEventLog
{
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly Lock _gate = new();
    private readonly Queue<(long Seq, string Json)> _recent = new();
    private long _next = 1;

    public void Write<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        _output.WriteLine(json);
        lock (_gate)
        {
            _recent.Enqueue((_next++, json));
            if (_recent.Count > capacity) _recent.Dequeue();
        }
    }

    /// <summary>The sequence number the next event will get. Read it before a test, then ask for events after it.</summary>
    public long Next { get { lock (_gate) return _next; } }

    /// <summary>
    /// Events with a sequence number of at least <paramref name="from"/>, optionally only those whose
    /// "Event" field is <paramref name="type"/>, as {"Next":n,"Events":[...]}. The events are already
    /// JSON, so they are spliced in as is.
    /// </summary>
    public string Read(long from, string? type, int limit = 10_000)
    {
        var marker = type is null ? null : $"\"Event\":\"{type}\"";
        var builder = new System.Text.StringBuilder();
        lock (_gate)
        {
            builder.Append("{\"Next\":").Append(_next).Append(",\"Events\":[");
            var count = 0;
            foreach (var (seq, json) in _recent)
            {
                if (seq < from || (marker is not null && !json.Contains(marker, StringComparison.Ordinal))) continue;
                if (count++ > 0) builder.Append(',');
                builder.Append(json);
                if (count >= limit) break;
            }
        }
        return builder.Append("]}").ToString();
    }
}

/// <summary>
/// Turns one Proxylity batch into zero or more replies. Many flows can share a batch, and one flow
/// spans many batches; the <see cref="FlowTable"/> keeps the memory that ties them together.
/// </summary>
public sealed class BatchProcessor(FlowTable table, IEventLog log, Func<AppSettings> settings, Func<string> microvmId)
{
    // The FIN is a negative sequence. Decoding only the first few bytes of each datagram is
    // enough; a 1470 byte payload is never fully decoded.
    private const int PrefixChars = 32; // 24 bytes: header, first-packet flags, padding

    private long _batches;
    private long _packets;

    public long Batches => Interlocked.Read(ref _batches);
    public long Packets => Interlocked.Read(ref _packets);

    public GatewayResponse Process(GatewayRequest request)
    {
        var messages = request.Messages ?? [];
        var batchNumber = Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _packets, messages.Length);

        var data = new Dictionary<FlowKey, List<Arrival>>();
        var fins = new List<(FlowKey Key, string Tag, FinArrival Fin)>();
        var dropped = 0;
        string? dropReason = null;
        string? firstReceivedAt = null, lastReceivedAt = null;
        var wholeSeconds = 0; // timestamps with no fractional part: a sign of a coarse ReceivedAt
        long firstUs = long.MaxValue, lastUs = long.MinValue;

        foreach (var message in messages)
        {
            if (!TryParse(message, out var key, out var datagram, out var length, out var reason))
            {
                dropped++;
                dropReason ??= reason;
                continue;
            }
            if (!TryParseReceivedAt(message.ReceivedAt, out var receivedUs))
            {
                dropped++;
                dropReason ??= "missing or invalid ReceivedAt";
                continue;
            }
            if (receivedUs % 1_000_000 == 0) wholeSeconds++;
            if (receivedUs < firstUs) { firstUs = receivedUs; firstReceivedAt = message.ReceivedAt; }
            if (receivedUs > lastUs) { lastUs = receivedUs; lastReceivedAt = message.ReceivedAt; }
            if (datagram.IsFinal)
            {
                fins.Add((key, message.Tag, new(datagram.AbsoluteSequence, receivedUs, datagram.Uses64BitSequence, length)));
                continue;
            }
            if (!data.TryGetValue(key, out var list)) data[key] = list = [];
            list.Add(new(datagram.Sequence, length, receivedUs, datagram.SentMicroseconds));
        }

        // Data first, then FINs, so a FIN in the same batch sees the data that preceded it.
        var flowLines = new List<FlowLine>(data.Count);
        foreach (var (key, arrivals) in data)
        {
            // The flow table orders arrivals by ReceivedAt (keeping this delivery order for ties).
            var counters = table.AddData(key, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(arrivals));
            if (counters is { } c)
                flowLines.Add(new(key.ToString(), c.Received, c.Bytes, c.HighestSequence, c.OutOfOrder, c.Duplicates, c.JitterMicroseconds, c.Pending));
        }

        var replies = new List<ResponsePacket>();
        var answered = new HashSet<FlowKey>();
        var id = microvmId();
        foreach (var (key, tag, fin) in fins.OrderBy(f => f.Fin.ReceivedMicroseconds))
        {
            // The client resends FIN every ~10 ms, so a batch often holds several. One reply per flow is enough.
            if (answered.Contains(key)) continue;
            var outcome = table.Fin(key, fin);
            log.Write(new FinEvent("fin", id, key.ToString(), fin.Sequence, outcome.Action.ToString().ToLowerInvariant()),
                AppJson.Default.FinEvent);
            if (outcome.Action is not (FinAction.Final or FinAction.Replay)) continue;
            answered.Add(key);

            var report = Iperf2Protocol.WriteServerReport(outcome.Uses64BitSequence, outcome.Report);
            var hex = Convert.ToHexString(report);
            log.Write(new ReportEvent("report", id, key.ToString(), outcome.Report.ReceivedBytes, outcome.Report.DurationMicroseconds,
                outcome.Report.LostDatagrams, outcome.Report.OutOfOrderDatagrams, outcome.Report.Datagrams,
                outcome.Duplicates, outcome.Report.JitterMicroseconds, outcome.Action == FinAction.Replay, hex),
                AppJson.Default.ReportEvent);
            replies.Add(new(tag, Convert.ToBase64String(report)));
        }

        if (settings().LogBatches ?? true)
            log.Write(new BatchEvent("batch", id, batchNumber, messages.Length, dropped, dropReason,
                firstReceivedAt, lastReceivedAt, DateTimeOffset.UtcNow.ToString("O"), wholeSeconds, [.. flowLines]),
                AppJson.Default.BatchEvent);

        return new([.. replies]);
    }

    private static bool TryParse(RequestPacket message, out FlowKey key, out IperfDatagram datagram, out int length, out string? reason)
    {
        key = default;
        datagram = default;
        length = 0;
        reason = null;

        if (message.Remote is null || message.Local is null || string.IsNullOrEmpty(message.Data))
        {
            reason = "incomplete message";
            return false;
        }
        if (!string.Equals(message.Formatter ?? "base64", "base64", StringComparison.OrdinalIgnoreCase))
        {
            reason = "formatter must be base64";
            return false;
        }

        var text = message.Data.AsSpan();
        var prefix = text[..Math.Min(text.Length, PrefixChars)];
        Span<byte> buffer = stackalloc byte[PrefixChars / 4 * 3];
        if (!Convert.TryFromBase64Chars(prefix, buffer, out var written))
        {
            reason = "invalid base64";
            return false;
        }

        // Length of the whole payload without decoding it: 3 bytes per 4 characters, less padding.
        var padding = text.EndsWith("==") ? 2 : text.EndsWith('=') ? 1 : 0;
        length = text.Length / 4 * 3 - padding;

        if (!Iperf2Protocol.TryParse(buffer[..written], out datagram))
        {
            reason = "not an iperf2 datagram";
            return false;
        }
        key = new(message.Remote.IpAddress, message.Remote.Port, message.Local.Port);
        return true;
    }

    /// <summary>ReceivedAt is ISO 8601 text. Kept as a string in the model so the raw value can be logged as delivered.</summary>
    private static bool TryParseReceivedAt(string? text, out long unixMicroseconds)
    {
        unixMicroseconds = 0;
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var value)) return false;
        unixMicroseconds = (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        return true;
    }
}
