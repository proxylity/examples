using System.Text.Json.Serialization;

namespace Iperf2.MicroVm;

// ---- The Proxylity batch contract (JSON over HTTPS on port 8080) ----

public sealed record Endpoint(string IpAddress, int Port);
public sealed record LocalEndpoint(string Domain, int Port);
public sealed record RequestPacket(
    string Tag, Endpoint Remote, LocalEndpoint Local, string? Formatter, string? Data, string? ReceivedAt);
public sealed record GatewayRequest(RequestPacket[]? Messages);
public sealed record ResponsePacket(string Tag, string Data);
public sealed record GatewayResponse(ResponsePacket[] Replies);

// ---- The Lambda lifecycle hooks (port 9000) ----

/// <summary>Lambda posts this to the /run hook when a MicroVM starts.</summary>
public sealed record RunRequest(string? MicrovmId, string? RunHookPayload);

/// <summary>The payload Proxylity supplies in <c>runHookPayload</c>.</summary>
public sealed record RunHookPayload(
    int Version, string? DestinationName, string? ImageArn, string? TenantId, string? Region,
    string? Formatter, int Port, string? StartedAt, string? ExtraData);

/// <summary>Settings carried in the destination's RunHookExtraData argument (a JSON string).</summary>
public sealed record AppSettings(int? FinGraceMs, bool? LogBatches, int? IdleTimeoutSeconds)
{
    // Reserved for the reverse-mode follow-up: the PacketSource SNS topic ARN arrives here too.
    public string? PacketSourceTopicArn { get; init; }
}

// ---- Structured log events (one JSON line each, picked up by CloudWatch Logs) ----

public sealed record RunEvent(string Event, string Microvm, string? Tenant, string? Region, AppSettings Settings);
public sealed record FlowLine(string Flow, long Received, long Bytes, long Highest, long OutOfOrder, long Duplicates, long JitterUs, int Pending);
/// <summary>One per delivery. ReceivedAt shows the first and last raw timestamps exactly as delivered; ProcessedAt is the VM clock.</summary>
public sealed record BatchEvent(string Event, string Microvm, long Batch, int Messages, int Dropped, string? DropReason,
    string? FirstReceivedAt, string? LastReceivedAt, string ProcessedAt, int WholeSecondTimestamps, FlowLine[] Flows);
public sealed record FinEvent(string Event, string Microvm, string Flow, long FinSequence, string Action);
public sealed record ReportEvent(
    string Event, string Microvm, string Flow, long Bytes, long DurationUs, long Lost, long OutOfOrder,
    long Datagrams, long Duplicates, long JitterUs, bool Replay, string ReportHex);
public sealed record StatsEvent(string Event, string Microvm, int ActiveFlows, int CompletedFlows, long Evicted, long Batches, long Packets);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GatewayRequest))]
[JsonSerializable(typeof(GatewayResponse))]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunHookPayload))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(BatchEvent))]
[JsonSerializable(typeof(FinEvent))]
[JsonSerializable(typeof(ReportEvent))]
[JsonSerializable(typeof(StatsEvent))]
[JsonSerializable(typeof(StatusDocument))]
public partial class AppJson : JsonSerializerContext;

public sealed record StatusDocument(string Status, string Microvm, string? Tenant, int ActiveFlows, int CompletedFlows, long Batches, long Packets);
