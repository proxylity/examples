using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace JsonAdapterLambda;

public class Function(IAmazonLambda lambdaClient)
{
    static readonly string DNS_FILTER_FUNCTION_NAME = Environment.GetEnvironmentVariable(nameof(DNS_FILTER_FUNCTION_NAME)) 
        ?? throw new InvalidOperationException($"{nameof(DNS_FILTER_FUNCTION_NAME)} environment variable is not set");
    
    static readonly int UDP_LISTENER_PORT = int.Parse(Environment.GetEnvironmentVariable(nameof(UDP_LISTENER_PORT))
        ?? throw new InvalidOperationException($"{nameof(UDP_LISTENER_PORT)} environment variable is not set"));

    public Function() : this(new AmazonLambdaClient()) { }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await Console.Out.WriteLineAsync($"DoH request: {request.ToJsonString()}");
        try
        {
            string dnsMessage = ExtractDnsMessage(request);
            var dnsFilterPayload = CreateDnsFilterPayload(request, dnsMessage);

            var invokeRequest = new Amazon.Lambda.Model.InvokeRequest
            {
                FunctionName = DNS_FILTER_FUNCTION_NAME,
                Payload = JsonSerializer.Serialize(dnsFilterPayload)
            };

            var response = await lambdaClient.InvokeAsync(invokeRequest);
            var responsePayload = JsonNode.Parse(Encoding.UTF8.GetString(response.Payload.ToArray()))?.AsObject()
                ?? throw new InvalidOperationException("Invalid response from DNS filter lambda");

            var replies = responsePayload["Replies"]?.AsArray();
            if (replies?.Count > 0)
            {
                var dnsResponse = replies[0]?["Data"]?.ToString()
                    ?? throw new InvalidOperationException("No DNS response data found");

                await Console.Out.WriteLineAsync($"DoH response: {dnsResponse}");
                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/dns-message",
                        ["Cache-Control"] = "max-age=300" // TODO: should be set to the smallest TTL of the DNS response
                    },
                    Body = dnsResponse,
                    IsBase64Encoded = true
                };
            }

            return CreateErrorResponse(500, "No replies found in DNS filter response");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error processing DNS request: {ex}");
            return CreateErrorResponse(500, "Internal server error");
        }
    }

    private static string ExtractDnsMessage(APIGatewayProxyRequest request)
    {
        return request.HttpMethod.ToUpperInvariant() switch
        {
            "GET" => ExtractFromGetRequest(request),
            "POST" => ExtractFromPostRequest(request),
            _ => throw new InvalidOperationException("Method not allowed")
        };
    }

    private static string ExtractFromGetRequest(APIGatewayProxyRequest request)
    {
        var dnsParam = (request.QueryStringParameters?.TryGetValue("dns", out var p) == true ? p : null) ??
            throw new InvalidOperationException("Missing dns parameter");

        // Convert URL-safe base64 to standard base64
        var base64 = dnsParam.Replace('-', '+').Replace('_', '/');
        return (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            _ => base64
        };
    }

    private static string ExtractFromPostRequest(APIGatewayProxyRequest request)
    {
        if (string.IsNullOrEmpty(request.Body))
            throw new InvalidOperationException("Missing request body");

        return request.IsBase64Encoded ? request.Body : Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));
    }

    private static JsonObject CreateDnsFilterPayload(APIGatewayProxyRequest request, string dnsMessage)
    {
        // Put together a JSON object to send the the Lambda that is in the same format as UDP Gateway uses
        // so we can just reuse that Lambda and not duplicate code or create a shared library
        var message = new JsonObject
        {
            ["Tag"] = request.RequestContext.RequestId,  // unique withing the request
            ["Remote"] = new JsonObject
            {
                ["IpAddress"] = request.RequestContext.Identity.SourceIp,
                ["Port"] = 0 // client/src port is not provided by API Gateway
            },
            ["Local"] = new JsonObject
            {
                ["IpAddress"] = "0.0.0.0", // destination IP is not provided by API Gateway
                ["Port"] = UDP_LISTENER_PORT
            },
            ["ReceivedAt"] = DateTime.UtcNow.ToString("O"),
            ["Data"] = dnsMessage
        };

        return new JsonObject
        {
            ["Messages"] = new JsonArray { message }
        };
    }

    private static APIGatewayProxyResponse CreateErrorResponse(int statusCode, string message)
    {
        return new APIGatewayProxyResponse
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain"
            },
            Body = message
        };
    }
}
