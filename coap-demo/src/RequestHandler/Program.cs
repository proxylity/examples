using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using CoapDemo.Common.Protocol;

namespace RequestHandler;

public static class Program
{
    public static async Task Main()
    {
        var function = new Function();
        Func<ProxylityLambdaRequest, ILambdaContext, Task<ProxylityLambdaResponse>> handler = function.FunctionHandler;
        var builder = LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<CoapJsonContext>());
        await builder.Build().RunAsync();
    }
}
