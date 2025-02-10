using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using System.Diagnostics;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error))
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();

await Run<IMontonicGrain, SWMontonicGrain>(nameof(SWMontonicGrain), grainFactory.GetGrain<IMontonicGrain>(0));
//await Run<IAdaptiveGrain, SWAdaptiveGrain>(nameof(SWAdaptiveGrain), grainFactory.GetGrain<IAdaptiveGrain>(0));

Console.ReadKey();

async static Task Run<T, H>(string name, T grain)
    where T : IProcessorGrain
    where H : BaseGrain<H>
{
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(250);
            Console.WriteLine(
                $"{name} Active Workers (Current/Max/Avg): " +
                $"{BaseGrain<H>.GetActiveWorkers()}/" +
                $"{BaseGrain<H>.GetMaxActiveWorkers()}/" +
                $"{BaseGrain<H>.GetAverageActiveWorkers():F2}");
        }
    });

    var tasks = new List<Task>();
    for (var i = 0; i < 100; i++)
    {
        tasks.Add(grain.Process());
    }

    await Task.WhenAll(tasks);
}

public interface IProcessorGrain : IGrainWithIntegerKey
{
    Task Process();
}

public interface IMontonicGrain : IProcessorGrain { }
public interface IAdaptiveGrain : IProcessorGrain { }

[StatelessWorker(10, StatelessWorkerOperatingMode.Monotonic)]
public class SWMontonicGrain : BaseGrain<SWMontonicGrain>, IMontonicGrain;

[StatelessWorker(10, StatelessWorkerOperatingMode.Adaptive)]
public class SWAdaptiveGrain : BaseGrain<SWAdaptiveGrain>, IAdaptiveGrain;

public abstract class BaseGrain<T> : Grain, IProcessorGrain where T : BaseGrain<T>
{
    // Static fields are unique for each closed generic type.
    // e.g., BaseGrain<SWMontonicGrain> and BaseGrain<SWAdaptiveGrain>

    private static int _activeWorkers = 0;
    private static int _maxActiveWorkers = 0;
    private static long _totalWorkerTicks = 0;
    private static long _lastUpdateTicks = 0;

    // Track if the Stopwatch is running
    private static readonly Stopwatch Watch = new();
    private static int _watchStarted = 0;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Start the Stopwatch on the first activation
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
        return Task.CompletedTask;
    }

    private static void UpdateStats()
    {
        var currentWorkers = Interlocked.CompareExchange(ref _activeWorkers, 0, 0);

        // Update max workers
        int oldMax;
        do
        {
            oldMax = Volatile.Read(ref _maxActiveWorkers);
            if (currentWorkers <= oldMax) break;
        } while (Interlocked.CompareExchange(ref _maxActiveWorkers, currentWorkers, oldMax) != oldMax);

        // Update total worker-ticks
        long elapsedTicks = Watch.IsRunning ? Watch.Elapsed.Ticks : _lastUpdateTicks;
        long lastUpdateTicks = Interlocked.Exchange(ref _lastUpdateTicks, elapsedTicks);
        long elapsedSinceLastUpdate = elapsedTicks - lastUpdateTicks;

        Interlocked.Add(ref _totalWorkerTicks, currentWorkers * elapsedSinceLastUpdate);

        // Stop the Stopwatch when no workers are left
        if (currentWorkers == 0 && Watch.IsRunning)
        {
            Watch.Stop();
        }
    }

    public static double GetAverageActiveWorkers()
    {
        var totalWorkerTicks = Interlocked.Read(ref _totalWorkerTicks);
        double totalElapsedTicks = Watch.IsRunning ? Watch.Elapsed.Ticks : _lastUpdateTicks;

        return totalElapsedTicks == 0 ? 0 : totalWorkerTicks / totalElapsedTicks;
    }

    public Task Process() => Task.Delay(Random.Shared.Next(1, 3) * 1000);
    public static int GetActiveWorkers() => Volatile.Read(ref _activeWorkers);
    public static int GetMaxActiveWorkers() => Volatile.Read(ref _maxActiveWorkers);

}