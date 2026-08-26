using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime;
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
        var siloPort = GetInt32(args, 1, 11111);
        var gatewayPort = GetInt32(args, 2, 30000);
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseOrleans((_, siloBuilder) => siloBuilder.UseLocalhostClustering(
                siloPort,
                gatewayPort,
                primarySiloEndpoint: null))
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
        var gatewayPort = GetInt32(args, 3, 30000);
        var sampleCount = GetInt32(args, 4, 1);

        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseOrleansClient((_, clientBuilder) => clientBuilder.UseLocalhostClustering(gatewayPort))
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
        Console.WriteLine(
            $"PING_ENV processors={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} " +
            $"latencyMode={GCSettings.LatencyMode}");

        var throughputs = new double[sampleCount];
        long totalCompleted = 0;
        long totalFailures = 0;
        long totalAllocatedBytes = 0;
        double totalElapsedSeconds = 0;

        for (var sample = 0; sample < sampleCount; sample++)
        {
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
            var throughput = completed / elapsedSeconds;
            throughputs[sample] = throughput;
            totalCompleted += completed;
            totalFailures += failed;
            totalAllocatedBytes += allocatedBytes;
            totalElapsedSeconds += elapsedSeconds;

            Console.WriteLine(
                $"PING_SAMPLE index={sample} throughput={throughput:F2} completed={completed} " +
                $"seconds={elapsedSeconds:F3} failures={failed} concurrency={concurrency} " +
                $"allocatedBytes={allocatedBytes} bytesPerOperation={(double)allocatedBytes / completed:F2} " +
                $"gen0={GC.CollectionCount(0) - initialGen0Collections} " +
                $"gen1={GC.CollectionCount(1) - initialGen1Collections} " +
                $"gen2={GC.CollectionCount(2) - initialGen2Collections}");
        }

        await cancellation.CancelAsync();
        await Task.WhenAll(workers);

        Array.Sort(throughputs);
        var medianThroughput = throughputs.Length % 2 == 0
            ? (throughputs[(throughputs.Length / 2) - 1] + throughputs[throughputs.Length / 2]) / 2
            : throughputs[throughputs.Length / 2];

        Console.WriteLine(
            $"PING_RESULT throughput={medianThroughput:F2} completed={totalCompleted} " +
            $"seconds={totalElapsedSeconds:F3} failures={totalFailures} concurrency={concurrency} " +
            $"allocatedBytes={totalAllocatedBytes} bytesPerOperation={(double)totalAllocatedBytes / totalCompleted:F2} " +
            $"samples={sampleCount}");

        await host.StopAsync();
    }

    public static async Task RunLatencyClientAsync(string[] args)
    {
        var warmupSeconds = GetInt32(args, 0, 60);
        var measurementSeconds = GetInt32(args, 1, 20);
        var sampleCount = GetInt32(args, 2, 3);
        var gatewayPort = GetInt32(args, 3, 30000);

        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseOrleansClient((_, clientBuilder) => clientBuilder.UseLocalhostClustering(gatewayPort))
            .Build();

        await host.StartAsync();
        var client = host.Services.GetRequiredService<IClusterClient>();
        var grain = client.GetGrain<IPingGrain>(0);

        var warmupStart = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < warmupSeconds)
        {
            await grain.Run();
        }

        Console.WriteLine(
            $"PING_ENV processors={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} " +
            $"latencyMode={GCSettings.LatencyMode}");

        var durations = new long[2_000_000];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var count = 0;
            var sampleStart = Stopwatch.GetTimestamp();
            while (count < durations.Length
                && Stopwatch.GetElapsedTime(sampleStart).TotalSeconds < measurementSeconds)
            {
                var start = Stopwatch.GetTimestamp();
                await grain.Run();
                durations[count++] = Stopwatch.GetTimestamp() - start;
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(sampleStart).TotalSeconds;
            Array.Sort(durations, 0, count);
            var tickToMicroseconds = 1_000_000d / Stopwatch.Frequency;
            Console.WriteLine(
                $"PING_LATENCY index={sample} count={count} throughput={count / elapsedSeconds:F2} " +
                $"p50us={Percentile(durations, count, 0.50) * tickToMicroseconds:F2} " +
                $"p95us={Percentile(durations, count, 0.95) * tickToMicroseconds:F2} " +
                $"p99us={Percentile(durations, count, 0.99) * tickToMicroseconds:F2}");
        }

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

    private static long Percentile(long[] values, int count, double percentile)
        => values[Math.Min(count - 1, (int)Math.Ceiling(count * percentile) - 1)];

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(0)]
        public long Value;
    }
}
