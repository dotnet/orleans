using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Concurrency;

namespace Benchmarks.Ping;

public class StatelessWorkerBenchmark : IDisposable
{
    private readonly IHost _host;
    private readonly IGrainFactory _grainFactory;

    public StatelessWorkerBenchmark()
    {
        _host = new HostBuilder()
            .UseOrleans((_, siloBuilder) => siloBuilder
            .UseLocalhostClustering())
            .Build();

        _host.StartAsync().GetAwaiter().GetResult();
        _grainFactory = _host.Services.GetRequiredService<IGrainFactory>();
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }

    public async Task RunAsync()
    {
        await Run<IMontonicGrain, SWMontonicGrain>(_grainFactory.GetGrain<IMontonicGrain>(0));
        await Run<IAdaptiveGrain, SWAdaptiveGrain>(_grainFactory.GetGrain<IAdaptiveGrain>(0));
    }

    private async static Task Run<T, H>(T grain)
        where T : IProcessorGrain
        where H : BaseGrain<H>
    {
        Console.WriteLine($"Executing benchmark for {typeof(H).Name}");

        using var cts = new CancellationTokenSource();

        var statsCollector = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(1, cts.Token);
                BaseGrain<H>.UpdateStats();
            }
        }, cts.Token);

        var tasks = new List<Task>();

        const int ConcurrencyLevel = 100;

        for (var i = 0; i < ConcurrencyLevel; i++)
        {
            tasks.Add(grain.Process());
        }

        await Task.WhenAll(tasks);

        var cooldownMs = Math.Ceiling(1.5 * BenchmarkStatics.ProcessDelayMs *
            ((double)ConcurrencyLevel / BenchmarkStatics.MaxWorkersLimit));

        var cooldownPeriod = TimeSpan.FromMilliseconds(cooldownMs);
        Console.WriteLine($"Waiting for cooldown period {cooldownPeriod}\n");

        await Task.Delay(cooldownPeriod);

        cts.Cancel();

        try
        {
            await statsCollector;
        }
        catch (OperationCanceledException)
        {

        }

        BaseGrain<H>.Stop();

        Console.WriteLine($"{typeof(H).Name} Stats:");
        Console.WriteLine($" Active Workers:  {BaseGrain<H>.GetActiveWorkers()}");
        Console.WriteLine($" Maximum Workers: {BaseGrain<H>.GetMaxActiveWorkers()}");
        Console.WriteLine($" Average Workers: {BaseGrain<H>.GetAverageActiveWorkers():F2}");
        Console.Write("\n\n");
    }

    public static class BenchmarkStatics
    {
        public const int MaxWorkersLimit = 10;
        public const int ProcessDelayMs = 1000;
    }

    public interface IProcessorGrain : IGrainWithIntegerKey
    {
        Task Process();
    }

    public interface IAdaptiveGrain : IProcessorGrain { }
    public interface IMontonicGrain : IProcessorGrain { }

    [StatelessWorker(BenchmarkStatics.MaxWorkersLimit, StatelessWorkerOperatingMode.Adaptive)]
    public class SWAdaptiveGrain : BaseGrain<SWAdaptiveGrain>, IAdaptiveGrain { }

    [StatelessWorker(BenchmarkStatics.MaxWorkersLimit, StatelessWorkerOperatingMode.Monotonic)]
    public class SWMontonicGrain : BaseGrain<SWMontonicGrain>, IMontonicGrain { }

    public abstract class BaseGrain<T> : Grain, IProcessorGrain where T : BaseGrain<T>
    {
        private static int _activeWorkers = 0;
        private static int _maxActiveWorkers = 0;
        private static long _totalWorkerTicks = 0;
        private static long _lastUpdateTicks = 0;

        private static int _watchStarted = 0;
        private static int _watchStopped = 0;

        private static readonly Stopwatch Watch = new();

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _watchStarted, 1, 0) == 0)
            {
                Watch.Start();
            }

            Interlocked.Increment(ref _activeWorkers);
            UpdateStats();

            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            Interlocked.Decrement(ref _activeWorkers);
            UpdateStats();

            if (Volatile.Read(ref _activeWorkers) == 0 &&
                Interlocked.CompareExchange(ref _watchStopped, 1, 0) == 0)
            {
                Watch.Stop();
            }

            return Task.CompletedTask;
        }

        public Task Process() => Task.Delay(BenchmarkStatics.ProcessDelayMs);

        public static void UpdateStats()
        {
            var currentWorkers = Volatile.Read(ref _activeWorkers);

            int oldMax;
            do
            {
                oldMax = Volatile.Read(ref _maxActiveWorkers);
                if (currentWorkers <= oldMax)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _maxActiveWorkers, currentWorkers, oldMax) != oldMax);

            var elapsedTicks = Watch.Elapsed.Ticks;
            var previousUpdate = Interlocked.Exchange(ref _lastUpdateTicks, elapsedTicks);
            var elapsedSinceLastUpdate = elapsedTicks - previousUpdate;

            Interlocked.Add(ref _totalWorkerTicks, currentWorkers * elapsedSinceLastUpdate);
        }

        public static void Stop()
        {
            UpdateStats();
            Watch.Stop();
        }

        public static int GetActiveWorkers() => Volatile.Read(ref _activeWorkers);
        public static int GetMaxActiveWorkers() => Volatile.Read(ref _maxActiveWorkers);

        public static double GetAverageActiveWorkers()
        {
            var totalTicks = Volatile.Read(ref _totalWorkerTicks);
            double elapsedTicks = Watch.Elapsed.Ticks;

            return elapsedTicks == 0 ? 0 : totalTicks / elapsedTicks;
        }
    }
}