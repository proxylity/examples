using System.Text.Json;
using Iperf2.MicroVm;
using Xunit;

namespace Iperf2.Tests;

public sealed class ContractTests
{
    [Fact]
    public void DeserializesProxylityBatch()
    {
        const string json = """
        {"Messages":[{"Tag":"t1","Remote":{"IpAddress":"203.0.113.10","Port":45678},
          "Local":{"Domain":"ingress-1","Port":15678},"Formatter":"base64","Data":"aGVsbG8=",
          "ReceivedAt":"2026-11-09T12:00:00.1234567Z"}]}
        """;
        var request = JsonSerializer.Deserialize(json, AppJson.Default.GatewayRequest);
        var message = Assert.Single(request!.Messages!);
        Assert.Equal("t1", message.Tag);
        Assert.Equal(45678, message.Remote.Port);
        Assert.Equal(15678, message.Local.Port);
        Assert.Equal("2026-11-09T12:00:00.1234567Z", message.ReceivedAt);
    }

    [Fact]
    public void SerializesRepliesWithProxylityNames()
    {
        var json = JsonSerializer.Serialize(new GatewayResponse([new("t1", "AAEC")]), AppJson.Default.GatewayResponse);
        Assert.Equal("""{"Replies":[{"Tag":"t1","Data":"AAEC"}]}""", json);
    }

    [Fact]
    public void ParsesRunHookPayloadAndExtraData()
    {
        const string body = """{"microvmId":"mvm-1","runHookPayload":"{\"Version\":1,\"TenantId\":\"iperf2-1\",\"ExtraData\":\"{\\\"FinGraceMs\\\":200}\"}"}""";
        var run = JsonSerializer.Deserialize(body, AppJson.Default.RunRequest)!;
        Assert.Equal("mvm-1", run.MicrovmId);
        var payload = JsonSerializer.Deserialize(run.RunHookPayload!, AppJson.Default.RunHookPayload)!;
        Assert.Equal("iperf2-1", payload.TenantId);
        var settings = JsonSerializer.Deserialize(payload.ExtraData!, AppJson.Default.AppSettings)!;
        Assert.Equal(200, settings.FinGraceMs);
    }
}
