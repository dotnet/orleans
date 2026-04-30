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
    private readonly IHost _clientHost;
    private readonly IClusterClient _client;
    private readonly BenchmarkMode _mode;
    private readonly int _numSilos;
    private readonly CancellationTokenSource _cts = new();

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

                // For SiloToSilo mode: remove grains from primary silo to force cross-silo calls
                if (i == 0 && grainsOnSecondariesOnly)
                {
                    siloBuilder.Configure<GrainTypeOptions>(options => options.Classes.Remove(typeof(PingGrain)));
                }
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
            ? _client
            : _hosts[0].Services.GetRequiredService<IGrainFactory>();
    }

    /// <summary>
    /// Runs the adaptive benchmark, tuning concurrency via hill climbing.
    /// Terminates after maxStableRounds without improvement (default 3), or runs forever if 0.
    /// </summary>
    public async Task RunAsync(
        int initialConcurrency = 100,
        int minConcurrency = 1,
        int maxConcurrency = 2000,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null,
        int maxStableRounds = 3)
    {
        var grainFactory = GetGrainFactory();

        Console.WriteLine($"=== Adaptive Ping Benchmark: {Description} ===");
        Console.WriteLine();

        var loadGenerator = new AdaptiveConcurrencyLoadGenerator<IPingGrain>(
            issueRequest: g => g.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId),
            requestsPerBlock: 500,
            warmupDuration: warmupDuration ?? TimeSpan.FromSeconds(5),
            measurementInterval: measurementInterval ?? TimeSpan.FromSeconds(5),
            minConcurrency: minConcurrency,
            maxConcurrency: maxConcurrency,
            initialConcurrency: initialConcurrency,
            maxStableRounds: maxStableRounds);

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
    public static async Task RunAllScenariosAsync(int maxStableRounds = 3)
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
}
