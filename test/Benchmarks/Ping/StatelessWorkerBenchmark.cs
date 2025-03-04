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
        Console.WriteLine($"Executing benchmark for '{typeof(H).Name}' with cooldown factor = {BenchmarkConstants.CooldownFactor:F1}");

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
        const double Lambda = 10.0d;

        for (var i = 0; i < ConcurrencyLevel; i++)
        {
            // For a Poisson process with rate λ (tasks / sec in our case), the time between arrivals is
            // exponentially distributed with density: f(t) = λe^(-λt), t >= 0; and the interarrival
            // time can be generated as: Δt = -ln(U) / λ, where U is uniformly distributed on (0, 1)

            var u = Random.Shared.NextDouble();
            var delaySec = -Math.Log(u > 0 ? u : double.Epsilon) / Lambda;
            var delayMs = (int)(1000 * delaySec);

            await Task.Delay(delayMs);
            tasks.Add(grain.Process());
        }

        await Task.WhenAll(tasks);

        var cooldownMs = Math.Ceiling(
            BenchmarkConstants.CooldownFactor * BenchmarkConstants.ProcessDelayMs *
            ((double)ConcurrencyLevel / BenchmarkConstants.MaxWorkersLimit));

        var cooldownPeriod = TimeSpan.FromMilliseconds(cooldownMs);
        Console.WriteLine($"\nWaiting {cooldownPeriod} for cooldown\n");

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

        Console.WriteLine("Stats:");
        Console.WriteLine($" Active Workers:  {BaseGrain<H>.GetActiveWorkers()}");
        Console.WriteLine($" Average Workers: {BaseGrain<H>.GetAverageActiveWorkers()}");
        Console.WriteLine($" Maximum Workers: {BaseGrain<H>.GetMaxActiveWorkers()}");
        Console.Write("\n---------------------------------------------------------------------\n");
    }

    public static class BenchmarkConstants
    {
        public const int MaxWorkersLimit = 10;
        public const int ProcessDelayMs = 1000;
        public const double CooldownFactor = 1.5;
    }

    public interface IProcessorGrain : IGrainWithIntegerKey
    {
        Task Process();
    }

    public interface IAdaptiveGrain : IProcessorGrain { }
    public interface IMontonicGrain : IProcessorGrain { }

    [StatelessWorker(BenchmarkConstants.MaxWorkersLimit, StatelessWorkerOperatingMode.Adaptive)]
    public class SWAdaptiveGrain : BaseGrain<SWAdaptiveGrain>, IAdaptiveGrain { }

    [StatelessWorker(BenchmarkConstants.MaxWorkersLimit, StatelessWorkerOperatingMode.Monotonic)]
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

        public Task Process() => Task.Delay(BenchmarkConstants.ProcessDelayMs);

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
            var elapsedTicks = Watch.Elapsed.Ticks;
            var result = elapsedTicks == 0 ? 0 : (double)totalTicks / elapsedTicks;

            return Math.Round(result, MidpointRounding.ToEven);
        }
    }
}