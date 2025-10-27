using System.Text.Json;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace RadiusChapLambda;

public static class Program
{
    public static async Task Main()
    {
        var function = new Function();
        var builder = Amazon.Lambda.RuntimeSupport.LambdaBootstrapBuilder.Create<CalculateChapHashRequest, CalculateChapHashResponse>(
            function.FunctionHandler,
            new SourceGeneratorLambdaJsonSerializer<JsonContext>()
        );
        await builder.Build().RunAsync();
    }
}