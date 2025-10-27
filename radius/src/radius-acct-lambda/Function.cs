using Amazon.Lambda.Core;
using Proxylity.UdpGateway.LambdaSdk;

// IMPORTANT: This assembly attribute is required! To use UdpGatewayBatchRequest and UdpGatewayBatchResponse, 
// the Lambda function must use the UdpGatewayLambdaJsonSerializer.  The default Lambda serializer does not 
// support these types due to a bug in the conversion of empty Base64 strings to byte[].
// vvv
[assembly: LambdaSerializer(typeof(UdpGatewayLambdaJsonSerializer))]
// ^^^

namespace RadiusAcctLambda;

public class Function
{
    static readonly Handler _handler = new();
    public static async Task<UdpGatewayBatchResponse> FunctionHandler(UdpGatewayBatchRequest request, ILambdaContext context)
    {
        return await UdpGatewayBatchProcessor.ProcessAsync(_handler, request, context);
    }
}
