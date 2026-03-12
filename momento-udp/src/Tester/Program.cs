using System.Diagnostics;
using System.Net;
using DotMake.CommandLine;
using MomentoUdpCache.Sdk;

/// <summary>Benchmark and correctness-test the Momento UDP cache proxy.</summary>
/// <param name="udpHost">Plain UDP listener host (IP address or DNS name). Use CacheEndpoint stack output.</param>
/// <param name="udpPort">Plain UDP listener port. Use CacheEndpoint stack output.</param>
/// <param name="wgHost">WireGuard listener host. Use CacheEndpointWg stack output. Omit to skip WG run.</param>
/// <param name="wgPort">WireGuard listener port. Use CacheEndpointWg stack output.</param>
/// <param name="wgServerKeyFile">Path to file containing the WireGuard server public key (use WireGuardPublicKey stack output).</param>
/// <param name="wgPrivateKeyFile">Path to file containing your WireGuard private key (output of `wg genkey`).</param>
/// <param name="concurrency">Number of concurrent workers for each throughput run.</param>
/// <param name="duration">Throughput test duration in seconds for each run.</param>
/// <param name="ttl">Cache TTL in seconds applied to seeded and SET keys.</param>
/// <param name="keyCount">Number of distinct keys to seed and operate against.</param>
await Cli.RunAsync(async (
    string udpHost,
    int    udpPort,
    string wgHost           = "",
    int    wgPort           = 0,
    string wgServerKeyFile  = "",
    string wgPrivateKeyFile = "",
    int    concurrency      = 25,
    int    duration         = 10,
    uint   ttl              = 300,
    int    keyCount         = 1000) =>
{
    bool runWg = wgHost != "" && wgPort != 0 && wgServerKeyFile != "" && wgPrivateKeyFile != "";

    Console.WriteLine($"UDP  endpoint : {udpHost}:{udpPort}");
    if (runWg)
        Console.WriteLine($"WG   endpoint : {wgHost}:{wgPort}");
    Console.WriteLine($"Concurrency={concurrency}  Duration={duration}s  Keys={keyCount}  TTL={ttl}s");
    Console.WriteLine();

    // -----------------------------------------------------------------------
    //  Phase 1: warm-up -- seed keys once through UDP (both listeners share
    //  the same Lambda and Momento cache, so WG will see the same state).
    // -----------------------------------------------------------------------

    Console.WriteLine($"=== Warm-up: seeding {keyCount} keys (UDP) ===");
    await using var udpClient = new UdpCacheClient(udpHost, udpPort, TimeSpan.FromSeconds(ttl));
    var seedSw = Stopwatch.StartNew();

    var seedResults = await Task.WhenAll(
        Enumerable.Range(0, keyCount).Select(i =>
            udpClient.SetAsync($"key:{i}", $"value:{i}")));

    seedSw.Stop();
    Console.WriteLine($"Seeded {keyCount} keys in {seedSw.ElapsedMilliseconds} ms  " +
                      $"(errors: {seedResults.Count(r => r is CacheSetResponse.Error)})");
    Console.WriteLine();

    // -----------------------------------------------------------------------
    //  Phase 2: benchmark -- run each client in turn and collect results
    // -----------------------------------------------------------------------

    var udpResult = await RunBenchmarkAsync("UDP", udpClient, concurrency, duration, keyCount);

    BenchmarkResult? wgResult = null;
    if (runWg)
    {
        var serverKey = File.ReadAllText(wgServerKeyFile).Trim();
        var privKey   = File.ReadAllText(wgPrivateKeyFile).Trim();
        await using var wgClient = new WgCacheClient(
            new IPEndPoint(Dns.GetHostAddresses(wgHost).First(), wgPort),
            peerPublicKey:  serverKey,
            myPrivateKey:   privKey,
            defaultTtl:     TimeSpan.FromSeconds(ttl));

        wgResult = await RunBenchmarkAsync("WireGuard", wgClient, concurrency, duration, keyCount);
    }

    // -----------------------------------------------------------------------
    //  Phase 3: correctness spot-check (UDP only -- same Lambda either way)
    // -----------------------------------------------------------------------

    Console.WriteLine("=== Correctness spot-check ===");

    const string checkKey = "tester:check";
    const string checkVal = "hello-momento";

    var setR = await udpClient.SetAsync(checkKey, checkVal);
    Console.WriteLine($"  SET  {checkKey} = \"{checkVal}\"  -> " +
        (setR is CacheSetResponse.Success ? "OK" : $"ERROR: {((CacheSetResponse.Error)setR).Message}"));

    var getR     = await udpClient.GetAsync(checkKey);
    var hit      = getR as CacheGetResponse.Hit;
    var received = hit?.ValueString ?? "(null)";
    var match    = received == checkVal;
    Console.WriteLine($"  GET  {checkKey}     -> " +
        (hit is not null ? $"HIT \"{received}\"" : $"MISS  error={(getR as CacheGetResponse.Error)?.Message}") +
        $"  [{(match ? "PASS" : $"FAIL -- expected \"{checkVal}\"")}]");

    var delR = await udpClient.DeleteAsync(checkKey);
    Console.WriteLine($"  DEL  {checkKey}     -> " +
        (delR is CacheDeleteResponse.Success ? "OK" : $"ERROR: {((CacheDeleteResponse.Error)delR).Message}"));

    var afterDel = await udpClient.GetAsync(checkKey);
    Console.WriteLine($"  GET  {checkKey} (after DEL) -> {(afterDel is CacheGetResponse.Hit ? "HIT (unexpected!)" : "MISS")}  " +
                      $"[{(afterDel is CacheGetResponse.Miss ? "PASS" : "FAIL")}]");

    // -----------------------------------------------------------------------
    //  Summary comparison
    // -----------------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("=== Summary ===");
    PrintResult(udpResult);
    if (wgResult is not null)
    {
        PrintResult(wgResult);
        double ratio = wgResult.Throughput / udpResult.Throughput * 100.0;
        Console.WriteLine($"  WG vs UDP throughput: {ratio:F1} %");
    }

    Console.WriteLine();
    bool allPassed = udpResult.Errors == 0 && match && afterDel is CacheGetResponse.Miss
                     && (wgResult is null || wgResult.Errors == 0);
    Console.WriteLine(allPassed ? "All checks passed." : "One or more checks FAILED.");
});

// ---------------------------------------------------------------------------
//  Benchmark helper
// ---------------------------------------------------------------------------

static async Task<BenchmarkResult> RunBenchmarkAsync(
    string label, CacheClientBase client,
    int concurrency, int duration, int keyCount)
{
    Console.WriteLine($"=== Throughput run: {label} ({duration}s, {concurrency} workers) ===");

    long totalOps       = 0;
    long totalHits      = 0;
    long totalMisses    = 0;
    long totalErrors    = 0;
    long totalLatencyMs = 0;

    using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
    var       runSw  = Stopwatch.StartNew();

    var workers = Enumerable.Range(0, concurrency).Select(workerId => Task.Run(async () =>
    {
        var rng = new Random(workerId);
        long localOps = 0, localHits = 0, localMisses = 0, localErrors = 0, localLatMs = 0;

        while (!cts.Token.IsCancellationRequested)
        {
            var key  = $"key:{rng.Next(keyCount)}";
            var opSw = Stopwatch.StartNew();
            try
            {
                if (localOps % 2 == 0)
                {
                    var r = await client.GetAsync(key, cts.Token);
                    opSw.Stop();
                    if      (r is CacheGetResponse.Hit)  localHits++;
                    else if (r is CacheGetResponse.Miss) localMisses++;
                    else                                  localErrors++;
                }
                else
                {
                    var r = await client.SetAsync(key, $"v:{workerId}:{localOps}", ct: cts.Token);
                    opSw.Stop();
                    if (r is CacheSetResponse.Error) localErrors++;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { opSw.Stop(); localErrors++; }

            localOps++;
            localLatMs += opSw.ElapsedMilliseconds;
        }

        Interlocked.Add(ref totalOps,       localOps);
        Interlocked.Add(ref totalHits,      localHits);
        Interlocked.Add(ref totalMisses,    localMisses);
        Interlocked.Add(ref totalErrors,    localErrors);
        Interlocked.Add(ref totalLatencyMs, localLatMs);
    })).ToArray();

    var reporter = Task.Run(async () =>
    {
        long lastOps = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(1000);
            long snap  = Interlocked.Read(ref totalOps);
            long delta = snap - lastOps;
            lastOps    = snap;
            Console.WriteLine($"  [{runSw.Elapsed:mm\\:ss}]  {delta,6} ops/s   cumulative: {snap,8} ops");
        }
    });

    await Task.WhenAll(workers);
    runSw.Stop();
    await reporter;

    long   getOps     = totalHits + totalMisses;
    double elapsedSec = runSw.Elapsed.TotalSeconds;
    double throughput = totalOps / elapsedSec;
    double avgLatMs   = totalOps > 0 ? (double)totalLatencyMs / totalOps : 0;
    double hitRate    = getOps   > 0 ? (double)totalHits / getOps * 100.0 : 0;

    Console.WriteLine($"  Elapsed: {elapsedSec:F2} s  |  {throughput:N0} ops/s  |  " +
                      $"avg {avgLatMs:F2} ms  |  hit {hitRate:F1} %  |  errors {totalErrors:N0}");
    Console.WriteLine();

    return new BenchmarkResult(label, throughput, avgLatMs, hitRate, totalErrors);
}

static void PrintResult(BenchmarkResult r)
    => Console.WriteLine($"  {r.Label,-12} {r.Throughput,8:N0} ops/s   avg {r.AvgLatencyMs:F2} ms   " +
                         $"hit {r.HitRate:F1} %   errors {r.Errors:N0}");

record BenchmarkResult(string Label, double Throughput, double AvgLatencyMs, double HitRate, long Errors);
