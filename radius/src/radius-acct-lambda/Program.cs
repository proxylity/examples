using Proxylity.UdpGateway.LambdaSdk;

namespace RadiusAcctLambda;

public static class Program
{
    public static async Task Main()
    {
        var builder = Amazon.Lambda.RuntimeSupport.LambdaBootstrapBuilder.Create<UdpGatewayBatchRequest, UdpGatewayBatchResponse>(
            Function.FunctionHandler,
            new UdpGatewayLambdaJsonSerializer());
        await builder.Build().RunAsync();
    }
}