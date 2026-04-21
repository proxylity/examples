using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using PacketDotNet;

[assembly: LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<PacketProcessor.JsonContext>))]

namespace PacketProcessor;

public class Function
{
    private static readonly string AppsyncHttpUrl =
        Environment.GetEnvironmentVariable("APPSYNC_HTTP_URL")
        ?? throw new InvalidOperationException("APPSYNC_HTTP_URL environment variable is not set.");

    private static readonly string AppsyncApiKey =
        Environment.GetEnvironmentVariable("APPSYNC_API_KEY")
        ?? throw new InvalidOperationException("APPSYNC_API_KEY environment variable is not set.");

    private static readonly string AppsyncChannel =
        Environment.GetEnvironmentVariable("APPSYNC_CHANNEL")
        ?? throw new InvalidOperationException("APPSYNC_CHANNEL environment variable is not set.");

    private static readonly HttpClient Http = new();

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var sqsMessage in sqsEvent.Records)
        {
            try
            {
                var msg = JsonSerializer.Deserialize(sqsMessage.Body, JsonContext.Default.ProxylityMessage)
                    ?? throw new InvalidOperationException("Failed to deserialize Proxylity message.");

                byte[] payload  = Convert.FromBase64String(msg.Data ?? string.Empty);
                string protocol = msg.Local?.Protocol ?? "udp";

                var packetEvent = new PacketEvent
                {
                    CapturedAt  = DateTimeOffset.UtcNow.ToString("O"),
                    Protocol    = protocol,
                    SourceIp    = msg.Remote?.IpAddress ?? "unknown",
                    SourcePort  = msg.Remote?.Port ?? 0,
                    LengthBytes = payload.Length,
                    Layers      = PacketDissector.Dissect(protocol, msg.Remote, payload)
                };

                await PublishEventsAsync([packetEvent], context);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Failed to process message {sqsMessage.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = sqsMessage.MessageId });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    private static async Task PublishEventsAsync(PacketEvent[] events, ILambdaContext context)
    {
        // AppSync Events HTTP publish endpoint: POST https://{dns}/event
        var url = $"https://{AppsyncHttpUrl}/event";

        // Each element of the "events" array is published as an individual channel event.
        var body = new PublishBody
        {
            Channel = AppsyncChannel,
            Events = events.Select(e => JsonSerializer.Serialize(e, JsonContext.Default.PacketEvent)).ToArray()
        };

        var json = JsonSerializer.Serialize(body, JsonContext.Default.PublishBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", AppsyncApiKey);

        var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            context.Logger.LogError($"AppSync publish failed ({response.StatusCode}): {error}");
            response.EnsureSuccessStatusCode();
        }
    }
}

public class ProxylityMessage
{
    [JsonPropertyName("Tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("Remote")]
    public RemoteEndpoint? Remote { get; set; }

    [JsonPropertyName("Local")]
    public LocalEndpoint? Local { get; set; }

    [JsonPropertyName("Data")]
    public string? Data { get; set; }
}

public class RemoteEndpoint
{
    [JsonPropertyName("IpAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("Port")]
    public int Port { get; set; }
}

// ── AppSync event payload ────────────────────────────────────────────────────

public class PacketEvent
{
    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "udp";

    [JsonPropertyName("sourceIp")]
    public string SourceIp { get; set; } = string.Empty;

    [JsonPropertyName("sourcePort")]
    public int SourcePort { get; set; }

    [JsonPropertyName("lengthBytes")]
    public int LengthBytes { get; set; }

    [JsonPropertyName("layers")]
    public Layer[] Layers { get; set; } = [];
}

// ── AppSync publish body ─────────────────────────────────────────────────────

public class PublishBody
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("events")]
    public string[] Events { get; set; } = [];
}

// ── Supporting types ─────────────────────────────────────────────────────────

public class LocalEndpoint
{
    [JsonPropertyName("Protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("Port")]
    public int Port { get; set; }
}

public class Layer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Field[] Fields { get; set; } = [];
}

public class Field
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("subFields")]
    public Field[]? SubFields { get; set; }
}

// ── Packet dissector ─────────────────────────────────────────────────────────

internal static class PacketDissector
{
    internal static Layer[] Dissect(string? protocol, RemoteEndpoint? remote, byte[] payload)
    {
        var layers = new List<Layer>();

        if (string.Equals(protocol, "wg", StringComparison.OrdinalIgnoreCase))
        {
            // Outer WireGuard transport metadata
            layers.Add(new Layer
            {
                Name    = "WireGuard",
                Summary = $"WireGuard Transport, Peer: {remote?.IpAddress ?? "?"}:{remote?.Port ?? 0}",
                Fields  =
                [
                    new Field { Label = "Peer Address",  Value = remote?.IpAddress ?? "unknown" },
                    new Field { Label = "Peer Port",     Value = (remote?.Port ?? 0).ToString() },
                    new Field { Label = "Inner Payload", Value = $"{payload.Length} bytes" }
                ]
            });

            // Parse decapsulated inner IP packet with PacketDotNet
            try
            {
                var packet = Packet.ParsePacket(LinkLayers.Raw, payload);
                layers.AddRange(BuildPacketLayers(packet, payload));
            }
            catch
            {
                layers.Add(BuildDataLayer(payload));
            }
        }
        else
        {
            // Plain UDP — synthesise layers from Proxylity metadata; Data is the application payload
            layers.Add(new Layer
            {
                Name    = "IPv4",
                Summary = $"Internet Protocol Version 4, Src: {remote?.IpAddress ?? "?"}",
                Fields  =
                [
                    new Field { Label = "Source Address", Value = remote?.IpAddress ?? "unknown" },
                    new Field { Label = "Protocol",       Value = "UDP (17)" }
                ]
            });
            layers.Add(new Layer
            {
                Name    = "UDP",
                Summary = $"User Datagram Protocol, Src Port: {remote?.Port ?? 0}",
                Fields  =
                [
                    new Field { Label = "Source Port", Value = (remote?.Port ?? 0).ToString() },
                    new Field { Label = "Length",      Value = $"{payload.Length} bytes" }
                ]
            });
            layers.Add(BuildDataLayer(payload));
        }

        return [.. layers];
    }

    private static IEnumerable<Layer> BuildPacketLayers(Packet? start, byte[] rawPayload)
    {
        var current     = start;
        byte[]? finalPayload = rawPayload;

        while (current is not null)
        {
            Layer? layer = current switch
            {
                IPv4Packet   ip4  => BuildIPv4Layer(ip4),
                IPv6Packet   ip6  => BuildIPv6Layer(ip6),
                TcpPacket    tcp  => BuildTcpLayer(tcp),
                UdpPacket    udp  => BuildUdpLayer(udp),
                IcmpV4Packet icmp => BuildIcmpLayer(icmp),
                _                 => null
            };

            if (layer is not null)
            {
                yield return layer;
                finalPayload = current.PayloadData;
            }

            current = current.PayloadPacket;
        }

        if (finalPayload is { Length: > 0 })
            yield return BuildDataLayer(finalPayload);
    }

    private static Layer BuildIPv4Layer(IPv4Packet ip) => new()
    {
        Name    = "IPv4",
        Summary = $"Internet Protocol Version 4, Src: {ip.SourceAddress}, Dst: {ip.DestinationAddress}",
        Fields  =
        [
            new Field { Label = "Version",        Value = "4" },
            new Field { Label = "Header Length",  Value = $"{ip.HeaderLength} bytes" },
            new Field { Label = "Total Length",   Value = ip.TotalLength.ToString() },
            new Field { Label = "TTL",            Value = ip.TimeToLive.ToString() },
            new Field { Label = "Protocol",       Value = $"{ip.Protocol} ({(int)ip.Protocol})" },
            new Field { Label = "Source",         Value = ip.SourceAddress.ToString() },
            new Field { Label = "Destination",    Value = ip.DestinationAddress.ToString() }
        ]
    };

    private static Layer BuildIPv6Layer(IPv6Packet ip) => new()
    {
        Name    = "IPv6",
        Summary = $"Internet Protocol Version 6, Src: {ip.SourceAddress}, Dst: {ip.DestinationAddress}",
        Fields  =
        [
            new Field { Label = "Version",        Value = "6" },
            new Field { Label = "Payload Length", Value = ip.PayloadLength.ToString() },
            new Field { Label = "Next Header",    Value = $"{ip.Protocol} ({(int)ip.Protocol})" },
            new Field { Label = "Hop Limit",      Value = ip.HopLimit.ToString() },
            new Field { Label = "Source",         Value = ip.SourceAddress.ToString() },
            new Field { Label = "Destination",    Value = ip.DestinationAddress.ToString() }
        ]
    };

    private static Layer BuildTcpLayer(TcpPacket tcp)
    {
        var flagNames = new List<string>();
        if (tcp.Synchronize)    flagNames.Add("SYN");
        if (tcp.Acknowledgment) flagNames.Add("ACK");
        if (tcp.Finished)       flagNames.Add("FIN");
        if (tcp.Reset)          flagNames.Add("RST");
        if (tcp.Push)           flagNames.Add("PSH");
        if (tcp.Urgent)         flagNames.Add("URG");

        var flagSubFields = flagNames.Select(f => new Field { Label = f, Value = "Set" }).ToArray();

        return new Layer
        {
            Name    = "TCP",
            Summary = $"Transmission Control Protocol, Src Port: {tcp.SourcePort}, Dst Port: {tcp.DestinationPort}",
            Fields  =
            [
                new Field { Label = "Source Port",      Value = tcp.SourcePort.ToString() },
                new Field { Label = "Destination Port", Value = tcp.DestinationPort.ToString() },
                new Field { Label = "Sequence Number",  Value = tcp.SequenceNumber.ToString() },
                new Field { Label = "Acknowledgment",   Value = tcp.AcknowledgmentNumber.ToString() },
                new Field { Label = "Window Size",      Value = tcp.WindowSize.ToString() },
                new Field { Label = "Flags",
                            Value = flagNames.Count > 0 ? string.Join(", ", flagNames) : "none",
                            SubFields = flagSubFields.Length > 0 ? flagSubFields : null }
            ]
        };
    }

    private static Layer BuildUdpLayer(UdpPacket udp) => new()
    {
        Name    = "UDP",
        Summary = $"User Datagram Protocol, Src Port: {udp.SourcePort}, Dst Port: {udp.DestinationPort}",
        Fields  =
        [
            new Field { Label = "Source Port",      Value = udp.SourcePort.ToString() },
            new Field { Label = "Destination Port", Value = udp.DestinationPort.ToString() },
            new Field { Label = "Length",           Value = udp.Length.ToString() },
            new Field { Label = "Checksum",         Value = $"0x{udp.Checksum:x4}" }
        ]
    };

    private static Layer BuildIcmpLayer(IcmpV4Packet icmp) => new()
    {
        Name    = "ICMP",
        Summary = $"Internet Control Message Protocol, Type: {icmp.TypeCode}",
        Fields  =
        [
            new Field { Label = "Type/Code", Value = icmp.TypeCode.ToString() },
            new Field { Label = "Checksum",  Value = $"0x{icmp.Checksum:x4}" }
        ]
    };

    private static Layer BuildDataLayer(byte[] data)
    {
        var sb = new StringBuilder();
        const int LineWidth = 16;
        int limit = Math.Min(data.Length, 256);

        for (int i = 0; i < limit; i += LineWidth)
        {
            int end = Math.Min(i + LineWidth, limit);
            sb.Append($"{i:x4}  ");
            for (int j = i; j < end; j++) sb.Append($"{data[j]:x2} ");
            sb.AppendLine();
        }

        if (data.Length > 256)
            sb.Append($"... ({data.Length - 256} more bytes)");

        return new Layer
        {
            Name    = "Data",
            Summary = $"Data ({data.Length} bytes)",
            Fields  = [new Field { Label = "Hex Dump", Value = sb.ToString().TrimEnd() }]
        };
    }
}

// ── Source-gen serializer context ────────────────────────────────────────────

[JsonSerializable(typeof(ProxylityMessage))]
[JsonSerializable(typeof(PacketEvent))]
[JsonSerializable(typeof(Layer))]
[JsonSerializable(typeof(Field))]
[JsonSerializable(typeof(PublishBody))]
[JsonSerializable(typeof(SQSEvent))]
[JsonSerializable(typeof(SQSBatchResponse))]
internal partial class JsonContext : JsonSerializerContext;
