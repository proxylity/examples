using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace RadiusApiLambda;

public static class Program
{
    public static async Task Main()
    {
        Func<APIGatewayProxyRequest, ILambdaContext, Task<APIGatewayProxyResponse>> handler = Function.FunctionHandler;
        var builder = Amazon.Lambda.RuntimeSupport.LambdaBootstrapBuilder.Create(
            handler,
            new SourceGeneratorLambdaJsonSerializer<JsonContext>()
        );
        await builder.Build().RunAsync();
    }
}
