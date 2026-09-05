using System.Net;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;

namespace Benchmarks.Ping;

/// <summary>
/// Benchmark that runs indefinitely and uses hill climbing to tune concurrency
/// for maximum throughput. Useful for finding optimal concurrency levels and
/// for long-running performance testing.
/// </summary>
public class AdaptivePingBenchmark : IDisposable
{
    public enum BenchmarkMode
    {
        /// <summary>Client runs inside the silo process (lowest latency)</summary>
        HostedClient,
        /// <summary>External client connects to silo(s)</summary>
        ExternalClient,
        /// <summary>Calls go from one silo to another (tests cross-silo performance)</summary>
        SiloToSilo
    }

    private readonly List<IHost> _hosts = new();
    private readonly IHost? _clientHost;
    private readonly IClusterClient? _client;
    private readonly BenchmarkMode _mode;
    private readonly int _numSilos;
    private readonly CancellationTokenSource _cts = new();
    private const int DefaultRequestsPerBlock = 100;
    private const int DefaultInitialStepSize = 50;
    private const int DefaultMaxStableRounds = 5;
    private const double DefaultMinimumRelativeImprovement = 0.005;
    private static readonly TimeSpan DefaultMeasurementInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMilliseconds(250);

    public string Description { get; }
    public int BestConcurrency { get; private set; }
    public double BestThroughput { get; private set; }

    public AdaptivePingBenchmark(BenchmarkMode mode = BenchmarkMode.HostedClient, int numSilos = 1)
    {
        _mode = mode;
        _numSilos = numSilos;

        // Determine configuration based on mode
        bool startClient = mode == BenchmarkMode.ExternalClient;
        bool grainsOnSecondariesOnly = mode == BenchmarkMode.SiloToSilo;

        if (mode == BenchmarkMode.SiloToSilo && numSilos < 2)
        {
            numSilos = 2;
            _numSilos = 2;
        }

        Description = mode switch
        {
            BenchmarkMode.HostedClient => "Hosted Client",
            BenchmarkMode.ExternalClient when numSilos == 1 => "Client to Silo",
            BenchmarkMode.ExternalClient => $"Client to {numSilos} Silos",
            BenchmarkMode.SiloToSilo => "Silo to Silo",
            _ => mode.ToString()
        };

        // Start silos
        for (int i = 0; i < numSilos; i++)
        {
            var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
            var hostBuilder = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11111 + i,
                    gatewayPort: 30000 + i,
                    primarySiloEndpoint: primary);

                siloBuilder.Configure<GrainTypeOptions>(options =>
                {
                    options.Interfaces.Add(typeof(IPingGrain));
                    options.Classes.Add(typeof(PingGrain));

                    // For SiloToSilo mode: remove grains from primary silo to force cross-silo calls
                    if (i == 0 && grainsOnSecondariesOnly)
                    {
                        options.Classes.Remove(typeof(PingGrain));
                    }
                });
            });

            var host = hostBuilder.Build();
            host.StartAsync().GetAwaiter().GetResult();
            _hosts.Add(host);
        }

        // Wait for cluster to stabilize in multi-silo mode
        if (numSilos > 1)
        {
            Thread.Sleep(4000);
        }

        // Start external client if needed
        if (startClient)
        {
            var hostBuilder = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
            {
                if (numSilos == 1)
                {
                    clientBuilder.UseLocalhostClustering();
                }
                else
                {
                    var gateways = Enumerable.Range(30000, numSilos)
                        .Select(i => new IPEndPoint(IPAddress.Loopback, i))
                        .ToArray();
                    clientBuilder.UseStaticClustering(gateways);
                }
            });

            _clientHost = hostBuilder.Build();
            _clientHost.StartAsync().GetAwaiter().GetResult();
            _client = _clientHost.Services.GetRequiredService<IClusterClient>();

            // Warm up the client connection
            var grain = _client.GetGrain<IPingGrain>(0);
            grain.Run().AsTask().GetAwaiter().GetResult();
        }

        // Wire up Ctrl+C to cancel
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested...");
            _cts.Cancel();
        };
    }

    /// <summary>
    /// Gets the grain factory based on the current mode.
    /// </summary>
    private IGrainFactory GetGrainFactory()
    {
        return _mode == BenchmarkMode.ExternalClient
            ? _client!
            : _hosts[0].Services.GetRequiredService<IGrainFactory>();
    }

    /// <summary>
    /// Runs the adaptive benchmark, tuning concurrency via hill climbing.
    /// Terminates after maxStableRounds without a statistically significant improvement (default 5), or runs forever if 0.
    /// </summary>
    public async Task RunAsync(
        int initialConcurrency = 100,
        int minConcurrency = 1,
        int maxConcurrency = 2000,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null,
        int maxStableRounds = DefaultMaxStableRounds,
        int initialStepSize = DefaultInitialStepSize,
        TimeSpan? sampleInterval = null,
        double minimumRelativeImprovement = DefaultMinimumRelativeImprovement)
    {
        var grainFactory = GetGrainFactory();

        Console.WriteLine($"=== Adaptive Ping Benchmark: {Description} ===");
        Console.WriteLine();

        var loadGenerator = new AdaptiveConcurrencyLoadGenerator<IPingGrain>(
            issueRequest: g => g.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
            requestsPerBlock: DefaultRequestsPerBlock,
            warmupDuration: warmupDuration ?? TimeSpan.FromSeconds(5),
            measurementInterval: measurementInterval ?? DefaultMeasurementInterval,
            minConcurrency: minConcurrency,
            maxConcurrency: maxConcurrency,
            initialConcurrency: initialConcurrency,
            maxStableRounds: maxStableRounds,
            initialStepSize: initialStepSize,
            sampleInterval: sampleInterval ?? DefaultSampleInterval,
            minimumRelativeImprovement: minimumRelativeImprovement);

        try
        {
            await loadGenerator.RunForeverAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C
        }

        BestConcurrency = loadGenerator.BestConcurrency;
        BestThroughput = loadGenerator.BestThroughput;

        Console.WriteLine($"\nFinal best: {BestConcurrency} concurrency @ {BestThroughput:N0}/s");
    }

    public async Task ShutdownAsync()
    {
        if (_clientHost != null)
        {
            await _clientHost.StopAsync();
            if (_clientHost is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                _clientHost.Dispose();
        }

        _hosts.Reverse();
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                host.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        (_client as IDisposable)?.Dispose();
        _hosts.ForEach(h => h.Dispose());
    }

    /// <summary>
    /// Runs all adaptive ping benchmark scenarios and prints a summary.
    /// </summary>
    public static async Task RunAllScenariosAsync(int maxStableRounds = DefaultMaxStableRounds)
    {
        var results = new List<(string Description, int BestConcurrency, double BestThroughput)>();

        var scenarios = new (BenchmarkMode Mode, int NumSilos)[]
        {
            (BenchmarkMode.HostedClient, 1),
            (BenchmarkMode.ExternalClient, 1),
            (BenchmarkMode.ExternalClient, 2),
            (BenchmarkMode.SiloToSilo, 2),
        };

        foreach (var (mode, numSilos) in scenarios)
        {
            var benchmark = new AdaptivePingBenchmark(mode, numSilos);
            try
            {
                await benchmark.RunAsync(maxStableRounds: maxStableRounds);
                results.Add((benchmark.Description, benchmark.BestConcurrency, benchmark.BestThroughput));
            }
            finally
            {
                await benchmark.ShutdownAsync();
                benchmark.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 82));
            Console.WriteLine();

            GC.Collect();
            await Task.Delay(1000); // Brief pause between scenarios
        }

        // Print summary in GitHub-flavored markdown table format
        Console.WriteLine();
        Console.WriteLine("## Adaptive Ping Benchmark Results");
        Console.WriteLine();
        Console.WriteLine("| Scenario | Best Concurrency | Best Throughput |");
        Console.WriteLine("|----------|------------------|-----------------|");

        foreach (var (description, bestConcurrency, bestThroughput) in results)
        {
            Console.WriteLine($"| {description} | {bestConcurrency} | {bestThroughput:N0}/s |");
        }

        Console.WriteLine();
    }

    public static async Task RunDeterministicMatrixAsync(
        int repetitions = 5,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null)
    {
        var scenarios = new (BenchmarkMode Mode, int NumSilos)[]
        {
            (BenchmarkMode.HostedClient, 1),
            (BenchmarkMode.ExternalClient, 1),
            (BenchmarkMode.ExternalClient, 2),
            (BenchmarkMode.SiloToSilo, 2),
        };
        var results = new List<DeterministicResult>();

        foreach (var (mode, numSilos) in scenarios)
        {
            results.AddRange(await MeasureDeterministicScenarioAsync(mode, numSilos, repetitions, warmupDuration, measurementInterval));

            GC.Collect();
            await Task.Delay(1000);
        }

        PrintDeterministicResults(results);
    }

    public static async Task RunDeterministicScenarioAsync(
        BenchmarkMode mode,
        int numSilos,
        int repetitions = 5,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null)
    {
        var results = await MeasureDeterministicScenarioAsync(mode, numSilos, repetitions, warmupDuration, measurementInterval);
        PrintDeterministicResults(results);
    }

    private static async Task<List<DeterministicResult>> MeasureDeterministicScenarioAsync(
        BenchmarkMode mode,
        int numSilos,
        int repetitions,
        TimeSpan? warmupDuration,
        TimeSpan? measurementInterval)
    {
        int[] concurrencyLevels = [1, 16, 100, 250, 500];
        var results = new List<DeterministicResult>(concurrencyLevels.Length);
        using var benchmark = new AdaptivePingBenchmark(mode, numSilos);
        try
        {
            foreach (var concurrency in concurrencyLevels)
            {
                benchmark._cts.Token.ThrowIfCancellationRequested();
                var loadGenerator = new AdaptiveConcurrencyLoadGenerator<IPingGrain>(
                    issueRequest: grain => grain.Run(),
                    getStateForWorker: workerId => benchmark.GetGrainFactory().GetGrain<IPingGrain>(workerId),
                    requestsPerBlock: DefaultRequestsPerBlock,
                    warmupDuration: warmupDuration ?? TimeSpan.FromSeconds(5),
                    measurementInterval: measurementInterval ?? TimeSpan.FromSeconds(3),
                    minConcurrency: concurrency,
                    maxConcurrency: concurrency,
                    initialConcurrency: concurrency,
                    maxStableRounds: 1,
                    initialStepSize: 1,
                    sampleInterval: DefaultSampleInterval,
                    minimumRelativeImprovement: 0);
                var measurements = await loadGenerator.RunFixedConcurrencyAsync(repetitions, benchmark._cts.Token);
                var throughput = measurements.Select(static measurement => measurement.Throughput).Order().ToArray();
                var result = new DeterministicResult(
                    benchmark.Description,
                    concurrency,
                    GetPercentile(throughput, 0.50),
                    GetPercentile(throughput, 0.95),
                    GetPercentile(throughput, 0.99),
                    GetMedian(measurements, static measurement => measurement.LatencyP50Microseconds),
                    GetMedian(measurements, static measurement => measurement.LatencyP95Microseconds),
                    GetMedian(measurements, static measurement => measurement.LatencyP99Microseconds),
                    GetMedian(measurements, static measurement => measurement.AllocatedBytesPerRequest),
                    GetMedian(measurements, static measurement => measurement.Gen0CollectionsPerMillionRequests),
                    GetMedian(measurements, static measurement => measurement.CpuUtilization),
                    GetMedian(measurements, static measurement => measurement.LockContentionsPerMillionRequests));
                results.Add(result);
                Console.WriteLine(
                    $"{benchmark.Description}, concurrency {concurrency}: "
                    + $"P50 {result.P50Throughput:N0}/s, P95 {result.P95Throughput:N0}/s, "
                    + $"first-completion sample {result.LatencyP50Microseconds:N1}/{result.LatencyP95Microseconds:N1}/{result.LatencyP99Microseconds:N1} us");
            }
        }
        finally
        {
            await benchmark.ShutdownAsync();
        }

        return results;
    }

    private static void PrintDeterministicResults(List<DeterministicResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("## Deterministic Ping Benchmark Results");
        Console.WriteLine();
        Console.WriteLine("| Scenario | Concurrency | P50 throughput | P95 throughput | P99 throughput | First-completion sample P50/P95/P99 (us) | B/request | Gen0/M requests | CPU | Contentions/M requests |");
        Console.WriteLine("|----------|------------:|---------------:|---------------:|---------------:|-------------------------:|----------:|----------------:|----:|-----------------------:|");
        foreach (var result in results)
        {
            Console.WriteLine(
                $"| {result.Description} | {result.Concurrency} | {result.P50Throughput:N0}/s | "
                + $"{result.P95Throughput:N0}/s | {result.P99Throughput:N0}/s | "
                + $"{result.LatencyP50Microseconds:N1}/{result.LatencyP95Microseconds:N1}/{result.LatencyP99Microseconds:N1} | "
                + $"{result.AllocatedBytesPerRequest:N1} | {result.Gen0CollectionsPerMillionRequests:N2} | "
                + $"{result.CpuUtilization:N1}% | {result.LockContentionsPerMillionRequests:N2} |");
        }

        Console.WriteLine();
    }

    private static double GetPercentile(double[] sortedSamples, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sortedSamples.Length) - 1;
        return sortedSamples[Math.Clamp(index, 0, sortedSamples.Length - 1)];
    }

    private static double GetMedian(
        AdaptiveConcurrencyLoadGenerator<IPingGrain>.FixedConcurrencyMeasurement[] measurements,
        Func<AdaptiveConcurrencyLoadGenerator<IPingGrain>.FixedConcurrencyMeasurement, double> selector)
    {
        var values = measurements.Select(selector).Order().ToArray();
        var midpoint = values.Length / 2;
        return values.Length % 2 == 0 ? (values[midpoint - 1] + values[midpoint]) / 2 : values[midpoint];
    }

    private readonly record struct DeterministicResult(
        string Description,
        int Concurrency,
        double P50Throughput,
        double P95Throughput,
        double P99Throughput,
        double LatencyP50Microseconds,
        double LatencyP95Microseconds,
        double LatencyP99Microseconds,
        double AllocatedBytesPerRequest,
        double Gen0CollectionsPerMillionRequests,
        double CpuUtilization,
        double LockContentionsPerMillionRequests);
}
