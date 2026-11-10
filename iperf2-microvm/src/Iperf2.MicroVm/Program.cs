using System.Text.Json;
using Iperf2.MicroVm;

// Ports: Proxylity delivers packet batches to 8080. Lambda calls its lifecycle hooks on 9000, which is
// kept separate so the token Proxylity holds (scoped to 8080) cannot reach the hooks.
const int PacketPort = 8080;
const int HookPort = 9000;
const string RunHookPath = "/aws/lambda-microvms/runtime/v1/run";
const string ReadyHookPath = "/aws/lambda-microvms/runtime/v1/ready";

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders(); // structured events below are the only log output
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenAnyIP(PacketPort);
    options.ListenAnyIP(HookPort);
});

var app = builder.Build();

// Per-MicroVM values come from the /run hook, not from startup: Lambda snapshots the app after it
// starts, so anything computed at startup would be identical in every MicroVM.
var microvmId = "unknown";
string? tenantId = null;
var settings = new AppSettings(null, null, null);
var log = new EventLog();
var options = new FlowTableOptions();
var table = new FlowTable(options);
var processor = new BatchProcessor(table, log, () => settings, () => microvmId);
var ready = false;
app.Lifetime.ApplicationStarted.Register(() => ready = true);

// Each endpoint answers only on its own port. This checks the port the connection actually arrived
// on, not the HTTP Host header: Lambda and Proxylity don't send a Host header that names the port,
// so matching on Host (RequireHost) makes every request a 404.
static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> OnlyOnPort(int port) =>
    (context, next) => context.HttpContext.Connection.LocalPort == port
        ? next(context)
        : ValueTask.FromResult<object?>(Results.NotFound());

// Any request that ends in a 404 is logged, so a mismatch with the platform (a hook path, port, or
// method we didn't expect) shows up in CloudWatch instead of failing silently.
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.StatusCode == StatusCodes.Status404NotFound)
        Console.Error.WriteLine($"Unhandled request: {context.Request.Method} {context.Request.Path} " +
            $"on port {context.Connection.LocalPort} (Host: {context.Request.Host})");
});

// ---- Packet port ----

app.MapPost("/{**path}", async Task<IResult> (HttpContext context) =>
{
    GatewayRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync(AppJson.Default.GatewayRequest, context.RequestAborted);
    }
    catch (JsonException)
    {
        request = null;
    }
    if (request is null) return Results.BadRequest();
    // Process is synchronous: the flow table does its own locking, and nothing here awaits.
    return Results.Json(processor.Process(request), AppJson.Default.GatewayResponse);
}).AddEndpointFilter(OnlyOnPort(PacketPort));

// Recent events, read back from memory: GET /events?from=N&type=report. Proxylity only POSTs, so this is
// for people and the proof scripts (with a MicroVM auth token for port 8080). from=-1 returns only "Next".
app.MapGet("/events", (long? from, string? type) =>
    Results.Text(from == -1 ? $"{{\"Next\":{log.Next},\"Events\":[]}}" : log.Read(from ?? 0, type), "application/json"))
    .AddEndpointFilter(OnlyOnPort(PacketPort));

app.MapGet("/{**path}", () => Results.Json(
    new StatusDocument("ok", microvmId, tenantId, table.ActiveFlows, table.CompletedFlows, processor.Batches, processor.Packets),
    AppJson.Default.StatusDocument)).AddEndpointFilter(OnlyOnPort(PacketPort));

// ---- Hook port ----

// Lambda calls /ready while building the image and snapshots the app once it returns 200.
app.MapPost(ReadyHookPath, () => ready ? Results.Text("{}", "application/json") : Results.StatusCode(503))
    .AddEndpointFilter(OnlyOnPort(HookPort));

app.MapPost(RunHookPath, async (HttpContext context) =>
{
    try
    {
        var run = await context.Request.ReadFromJsonAsync(AppJson.Default.RunRequest, context.RequestAborted);
        microvmId = run?.MicrovmId ?? microvmId;
        RunHookPayload? payload = string.IsNullOrEmpty(run?.RunHookPayload)
            ? null
            : JsonSerializer.Deserialize(run.RunHookPayload, AppJson.Default.RunHookPayload);
        tenantId = payload?.TenantId;
        settings = string.IsNullOrWhiteSpace(payload?.ExtraData)
            ? settings
            : JsonSerializer.Deserialize(payload.ExtraData, AppJson.Default.AppSettings) ?? settings;
        if (settings.FinGraceMs is { } grace) options.FinalizationGrace = TimeSpan.FromMilliseconds(grace);
        if (settings.IdleTimeoutSeconds is { } idle) options.IdleTimeout = TimeSpan.FromSeconds(idle);
        log.Write(new RunEvent("run", microvmId, tenantId, payload?.Region, settings), AppJson.Default.RunEvent);
        return Results.Text("{}", "application/json");
    }
    catch (JsonException exception)
    {
        // Failing the hook stops Lambda routing traffic to a misconfigured MicroVM.
        Console.Error.WriteLine($"Run hook failed: {exception.Message}");
        return Results.StatusCode(500);
    }
}).AddEndpointFilter(OnlyOnPort(HookPort));


// ---- Housekeeping: drop idle flows, and say what is in memory ----

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    var wasBusy = false;
    while (await timer.WaitForNextTickAsync())
    {
        // Log while anything is in memory, plus one line after it empties (which shows the cleanup).
        var evicted = table.Evict();
        var busy = table.ActiveFlows > 0 || table.CompletedFlows > 0 || evicted > 0;
        if (busy || wasBusy)
            log.Write(new StatsEvent("stats", microvmId, table.ActiveFlows, table.CompletedFlows, table.Evicted,
                processor.Batches, processor.Packets), AppJson.Default.StatsEvent);
        wasBusy = busy;
    }
});

app.Run();
