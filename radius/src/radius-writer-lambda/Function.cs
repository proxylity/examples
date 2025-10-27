// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text.Json.Serialization;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: Amazon.Lambda.Core.LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<RadiusWriterLambda.JsonContext>))]

namespace RadiusWriterLambda;

public class Function()
{
    static readonly ReadOnlyMemory<byte> RADIUS_SHARED_SECRET = System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable(nameof(RADIUS_SHARED_SECRET)) ?? "not-so-secret");

    public async Task<RadiusWriterResponse> FunctionHandler(RadiusWriterRequest request, Amazon.Lambda.Core.ILambdaContext context)
    {
        context.Logger.LogLine($"Received request to write RADIUS packets: {request}");

        var packets = request.PacketsToWrite.Select(p =>
        {
            var authenticator = Convert.FromHexString(p.AuthenticatorHex);
            var attributes = DictionaryToRadiusAttributes.DictionaryToAttributes(p.Attributes, RADIUS_SHARED_SECRET, authenticator);
            var packet = RadiusPacketWriter.CreateResponsePacket(
                (byte)p.Code,
                (byte)p.Identifier,
                authenticator,
                attributes,
                RADIUS_SHARED_SECRET
            );
            return packet;
        }).ToArray();
        var base64s = packets.Select(Convert.ToBase64String).ToArray();
        return await Task.FromResult(new RadiusWriterResponse { PacketData = base64s });
    }
}

public class RadiusWriterRequest
{
    [JsonPropertyName("packets_to_write")]
    public PacketToWrite[] PacketsToWrite { get; set; } = [];
}

public class PacketToWrite
{
    [JsonPropertyName("code")]
    required public int Code { get; set; }

    [JsonPropertyName("request_identifier")]
    required public int Identifier { get; set; }

    [JsonPropertyName("request_authenticator")]
    required public string AuthenticatorHex { get; set; }

    [JsonPropertyName("attributes")]
    required public Dictionary<string, string> Attributes { get; set; }
}

public class RadiusWriterResponse
{
    [JsonPropertyName("packet_data")]
    required public string[] PacketData { get; set; }
}

[JsonSerializable(typeof(RadiusWriterRequest))]
[JsonSerializable(typeof(RadiusWriterResponse))]
[JsonSerializable(typeof(PacketToWrite))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public partial class JsonContext : JsonSerializerContext
{
}
