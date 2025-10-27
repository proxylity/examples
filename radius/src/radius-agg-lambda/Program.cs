using System.Text.Json;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace AuthAggregationLambda;

public static class Program
{
    public static async Task Main()
    {
        var function = new Function();
        var builder = Amazon.Lambda.RuntimeSupport.LambdaBootstrapBuilder.Create<AggregationRequest, AggregationResponse>(
            function.FunctionHandler,
            new SourceGeneratorLambdaJsonSerializer<JsonContext>()
        );
        await builder.Build().RunAsync();
    }
}