// A stand-in for the Proxylity UDP Gateway, for local development only.
//
// It listens on a UDP port, groups packets into batches (a count or a timeout, whichever comes
// first), POSTs each batch to the app in the same JSON format Proxylity uses, and sends the
// replies back to the original senders. Batches are posted concurrently, like the real service.
//
//   dotnet run --project tools/LocalGateway -- [udpPort=5001] [appUrl=http://localhost:8080/] [batchCount=10] [batchMs=100]
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

var udpPort = args.Length > 0 ? int.Parse(args[0]) : 5001;
var appUrl = args.Length > 1 ? args[1] : "http://localhost:8080/";
var batchCount = args.Length > 2 ? int.Parse(args[2]) : 10;
var batchMs = args.Length > 3 ? int.Parse(args[3]) : 100;

using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
// Like the real delivery path, the Host header names the endpoint, not the app's port.
http.DefaultRequestHeaders.Host = "microvm.example";
var channel = Channel.CreateUnbounded<(IPEndPoint From, byte[] Data, DateTimeOffset At)>();
var tags = new System.Collections.Concurrent.ConcurrentDictionary<string, IPEndPoint>();
long tagCounter = 0;
Console.WriteLine($"LocalGateway: udp/{udpPort} -> {appUrl} (batch {batchCount} / {batchMs} ms)");

_ = Task.Run(async () =>
{
    while (true)
    {
        var result = await udp.ReceiveAsync();
        await channel.Writer.WriteAsync((result.RemoteEndPoint, result.Buffer, DateTimeOffset.UtcNow));
    }
});

var batch = new List<(IPEndPoint From, byte[] Data, DateTimeOffset At)>();
while (true)
{
    batch.Clear();
    batch.Add(await channel.Reader.ReadAsync());
    var deadline = DateTime.UtcNow.AddMilliseconds(batchMs);
    while (batch.Count < batchCount)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) break;
        using var cts = new CancellationTokenSource(remaining);
        try { batch.Add(await channel.Reader.ReadAsync(cts.Token)); }
        catch (OperationCanceledException) { break; }
    }
    var packets = batch.ToArray(); // snapshot before the list is reused
    _ = Task.Run(() => Deliver(packets));
}

async Task Deliver((IPEndPoint From, byte[] Data, DateTimeOffset At)[] packets)
{
    var messages = packets.Select(p =>
    {
        var tag = Interlocked.Increment(ref tagCounter).ToString();
        tags[tag] = p.From;
        return new
        {
            Tag = tag,
            Remote = new { IpAddress = p.From.Address.ToString(), Port = p.From.Port },
            Local = new { Domain = "local", Port = udpPort },
            Formatter = "base64",
            Data = Convert.ToBase64String(p.Data),
            ReceivedAt = p.At.UtcDateTime.ToString("O"), // ISO 8601 UTC, 100 ns precision, as Proxylity sends it
        };
    }).ToArray();
    try
    {
        var response = await http.PostAsync(appUrl, new StringContent(JsonSerializer.Serialize(new { Messages = messages }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var reply in document.RootElement.GetProperty("Replies").EnumerateArray())
        {
            if (!tags.TryRemove(reply.GetProperty("Tag").GetString()!, out var to)) continue;
            var data = Convert.FromBase64String(reply.GetProperty("Data").GetString()!);
            await udp.SendAsync(data, data.Length, to);
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Delivery failed: {exception.Message}");
    }
    foreach (var message in messages) tags.TryRemove(message.Tag, out _);
}
