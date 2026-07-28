using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using System.Text.Json.Serialization;

namespace AsyncResponder;

public static class Program
{
    public static async Task Main()
    {
        var function = new Function();
        Func<SQSEvent, ILambdaContext, Task> handler = function.FunctionHandler;
        var builder = LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<AsyncResponderJsonContext>());
        await builder.Build().RunAsync();
    }
}

[JsonSerializable(typeof(SQSEvent))]
internal partial class AsyncResponderJsonContext : JsonSerializerContext { }
