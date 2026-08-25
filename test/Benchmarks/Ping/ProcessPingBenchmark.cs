using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using BenchmarkGrainInterfaces.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Benchmarks.Ping;

internal static class ProcessPingBenchmark
{
    public static async Task RunServerAsync(string[] args)
    {
        var duration = GetInt32(args, 0, 240);
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseOrleans((_, siloBuilder) => siloBuilder.UseLocalhostClustering(
                siloPort: 11111,
                gatewayPort: 30000,
                primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111)))
            .Build();

        await host.StartAsync();
        Console.WriteLine("PING_SERVER_READY");
        await Task.Delay(TimeSpan.FromSeconds(duration));
        await host.StopAsync();
    }

    public static async Task RunClientAsync(string[] args)
    {
        var warmupSeconds = GetInt32(args, 0, 60);
        var measurementSeconds = GetInt32(args, 1, 120);
        var concurrency = GetInt32(args, 2, 250);

        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseOrleansClient((_, clientBuilder) => clientBuilder.UseLocalhostClustering())
            .Build();

        await host.StartAsync();
        var client = host.Services.GetRequiredService<IClusterClient>();
        var counters = new PaddedCounter[concurrency];
        var failures = new PaddedCounter[concurrency];
        var workers = new Task[concurrency];
        using var cancellation = new CancellationTokenSource();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < workers.Length; i++)
        {
            var workerId = i;
            var grain = client.GetGrain<IPingGrain>(workerId);
            workers[i] = RunWorker(workerId, grain, start.Task, counters, failures, cancellation.Token);
        }

        start.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(warmupSeconds));

        var initialCount = Sum(counters);
        var initialFailures = Sum(failures);
        var initialAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var initialGen0Collections = GC.CollectionCount(0);
        var initialGen1Collections = GC.CollectionCount(1);
        var initialGen2Collections = GC.CollectionCount(2);
        var startTimestamp = Stopwatch.GetTimestamp();

        await Task.Delay(TimeSpan.FromSeconds(measurementSeconds));

        var endTimestamp = Stopwatch.GetTimestamp();
        var completed = Sum(counters) - initialCount;
        var failed = Sum(failures) - initialFailures;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - initialAllocatedBytes;
        var elapsedSeconds = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalSeconds;

        await cancellation.CancelAsync();
        await Task.WhenAll(workers);

        Console.WriteLine(
            $"PING_RESULT throughput={completed / elapsedSeconds:F2} completed={completed} " +
            $"seconds={elapsedSeconds:F3} failures={failed} concurrency={concurrency} " +
            $"allocatedBytes={allocatedBytes} bytesPerOperation={(double)allocatedBytes / completed:F2} " +
            $"gen0={GC.CollectionCount(0) - initialGen0Collections} " +
            $"gen1={GC.CollectionCount(1) - initialGen1Collections} " +
            $"gen2={GC.CollectionCount(2) - initialGen2Collections}");

        await host.StopAsync();
    }

    private static async Task RunWorker(
        int workerId,
        IPingGrain grain,
        Task start,
        PaddedCounter[] counters,
        PaddedCounter[] failures,
        CancellationToken cancellationToken)
    {
        await start.ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await grain.Run().ConfigureAwait(false);
                counters[workerId].Value++;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                failures[workerId].Value++;
            }
        }
    }

    private static long Sum(PaddedCounter[] counters)
    {
        long result = 0;
        foreach (ref var counter in counters.AsSpan())
        {
            result += Volatile.Read(ref counter.Value);
        }

        return result;
    }

    private static int GetInt32(string[] args, int index, int defaultValue)
        => args.Length > index && int.TryParse(args[index], out var value) && value > 0 ? value : defaultValue;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(0)]
        public long Value;
    }
}
