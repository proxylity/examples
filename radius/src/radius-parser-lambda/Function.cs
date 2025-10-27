// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text.Json.Serialization;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: Amazon.Lambda.Core.LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<RadiusParserLambda.JsonContext>))]

namespace RadiusParserLambda;

public class Function()
{
    static readonly ReadOnlyMemory<byte> RADIUS_SHARED_SECRET = System.Text.Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable(nameof(RADIUS_SHARED_SECRET)) ?? "not-so-secret");

    public async Task<RadiusParserResponse> FunctionHandler(RadiusParserRequest request, Amazon.Lambda.Core.ILambdaContext context)
    {
        context.Logger.LogLine($"Received request to parse RADIUS packets: {string.Join("\n", request)}");
        var packetData = request.PacketData.Select(Convert.FromBase64String).ToArray();
        var parsedPackets = packetData.Select(d => (d, p: RadiusPacketReader.Parse(d))).ToArray();

        return await Task.FromResult(new RadiusParserResponse()
        {
            ParsedPackets = [.. parsedPackets.Select(a => new ParsedPacket
            {
                Code = a.p.code,
                Identifier = a.p.id,
                Length = a.p.length,
                LengthIsValid = a.p.length == a.d.Length,
                AuthenticatorHex = BitConverter.ToString(a.p.authenticator.ToArray()).Replace("-", ""),
                Attributes = RadiusPacketToDictionary.AttributesToDictionary(a.p.attributes, RADIUS_SHARED_SECRET, a.p.authenticator),
                VendorSpecificAttributes = RadiusPacketToDictionary.VendorSpecificAttributesToDictionary(a.p.vsas),
                MessageAuthenticatorIsValid = RadiusPacketReader.CheckMessageAuthenticator(a.d, RADIUS_SHARED_SECRET)
            })]
        });
    }
}

public class RadiusParserRequest
{
    [JsonPropertyName("packet_data")]
    public string[] PacketData { get; set; } = [];
}

public class RadiusParserResponse
{
    [JsonPropertyName("parsed_packets")]
    public ParsedPacket[] ParsedPackets { get; set; } = [];
}

public class ParsedPacket
{
    [JsonPropertyName("code")]
    required public int Code { get; set; }

    [JsonPropertyName("identifier")]
    required public int Identifier { get; set; }

    [JsonPropertyName("length")]
    required public int Length { get; set; }

    [JsonPropertyName("length_is_valid")]
    required public bool LengthIsValid { get; set; }

    [JsonPropertyName("authenticator")]
    required public string AuthenticatorHex { get; set; }

    [JsonPropertyName("attributes")]
    required public Dictionary<string, string> Attributes { get; set; }

    [JsonPropertyName("vendor_specific_attributes")]
    required public Dictionary<string, string> VendorSpecificAttributes { get; set; }

    [JsonPropertyName("message_authenticator_is_valid")]
    required public bool? MessageAuthenticatorIsValid { get; set; } = null;
}

[JsonSerializable(typeof(RadiusParserRequest))]
[JsonSerializable(typeof(RadiusParserResponse))]
[JsonSerializable(typeof(ParsedPacket))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public partial class JsonContext : JsonSerializerContext
{
}
