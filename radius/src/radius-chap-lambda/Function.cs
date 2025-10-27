// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
using System.Text.Json.Serialization;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: Amazon.Lambda.Core.LambdaSerializer(typeof(SourceGeneratorLambdaJsonSerializer<RadiusChapLambda.JsonContext>))]

namespace RadiusChapLambda;

public class Function()
{
    public async Task<CalculateChapHashResponse> FunctionHandler(CalculateChapHashRequest request, Amazon.Lambda.Core.ILambdaContext context)
    {
        context.Logger.LogLine($"Received request to calculate CHAP hash for CHAP-Password: {request.ChapPasswordHex}, CHAP-Challenge: {request.ChapChallengeHex}");

        var user_password_bytes = System.Text.Encoding.UTF8.GetBytes(request.UserPasswordHex);
        var chap_password_bytes = Convert.FromHexString(request.ChapPasswordHex);
        var chap_challenge_bytes = Convert.FromHexString(request.ChapChallengeHex);

        var chap_id = chap_password_bytes[0];
        var hash_input = new byte[1 + user_password_bytes.Length + chap_challenge_bytes.Length];
        hash_input[0] = chap_id;
        user_password_bytes.CopyTo(hash_input, 1);
        chap_challenge_bytes.CopyTo(hash_input, 1 + user_password_bytes.Length);

        var chap_hash_bytes = System.Security.Cryptography.MD5.HashData(hash_input);
        var authenticated = chap_hash_bytes.SequenceEqual(chap_password_bytes[1..]);
        var chap_hash_hex = Convert.ToHexString(chap_hash_bytes).ToLowerInvariant();

        return await Task.FromResult(new CalculateChapHashResponse { ChapHashHex = chap_hash_hex, Authenticated = authenticated });
    }
}

public class CalculateChapHashRequest
{
    [JsonPropertyName("user_password")]
    public string UserPasswordHex { get; set; } = string.Empty;

    [JsonPropertyName("chap_password")]
    public string ChapPasswordHex { get; set; } = string.Empty;

    [JsonPropertyName("chap_challenge")]
    public string ChapChallengeHex { get; set; } = string.Empty;
}

public class CalculateChapHashResponse
{
    [JsonPropertyName("chap_hash")]
    public string ChapHashHex { get; set; } = string.Empty;

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; } = false;
}

[JsonSerializable(typeof(CalculateChapHashRequest))]
[JsonSerializable(typeof(CalculateChapHashResponse))]
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class JsonContext : JsonSerializerContext
{
}
