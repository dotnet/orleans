using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Concurrency;

namespace Benchmarks.Ping;

[Config(typeof(Config))]
public class StatelessWorkerBenchmark : IDisposable
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun);
            AddDiagnoser(MemoryDiagnoser.Default);
            AddColumnProvider(new WorkerColumnProvider());
        }
    }

    private const int MaxConcurrency = 100;

    private readonly IHost _host;
    private readonly IMontonicGrain _montonicGrain;
    private readonly IAdaptiveGrain _adaptiveGrain;
    private readonly CancellationTokenSource _cts = new();

    public StatelessWorkerBenchmark()
    {
        var hostBuilder = new HostBuilder().UseOrleans((_, siloBuilder) =>
            siloBuilder.UseLocalhostClustering());

        _host = hostBuilder.Build();
        _host.StartAsync().GetAwaiter().GetResult();

        var grainFactory = _host.Services.GetRequiredService<IGrainFactory>();

        _montonicGrain = grainFactory.GetGrain<IMontonicGrain>(0);
        _adaptiveGrain = grainFactory.GetGrain<IAdaptiveGrain>(0);

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(250);
                Console.WriteLine(
                    $"Active Workers (Current/Max/Avg): " +
                    $"{BaseGrain<SWMontonicGrain>.GetActiveWorkers()}/" +
                    $"{SWMontonicGrain.GetMaxActiveWorkers()}/" +
                    $"{SWMontonicGrain.GetAverageActiveWorkers():F2}");
            }
        });
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _cts.Cancel();
        _host.StopAsync().GetAwaiter().GetResult();
    }

    [Benchmark] public Task Monotonic() => Run(_montonicGrain);
    [Benchmark] public Task Adaptive() => Run(_adaptiveGrain);

    private async static Task Run<T>(T grain) where T : IProcessorGrain
    {
        var tasks = new List<Task>();
        for (var i = 0; i < MaxConcurrency; i++)
        {
            tasks.Add(grain.Process());
        }

        await Task.WhenAll(tasks);
    }
}

public class WorkerColumnProvider : IColumnProvider
{
    public IEnumerable<IColumn> GetColumns(Summary summary)
    {
        yield return new MaxWorkersColumn();
        yield return new AvgWorkersColumn();
    }
}

public class MaxWorkersColumn : IColumn
{
    public string Id => "MaxWorkers";
    public string ColumnName => "Max Workers";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 10;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Maximum number of active workers during the benchmark";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        if (benchmarkCase.Descriptor.WorkloadMethod.Name == nameof(StatelessWorkerBenchmark.Monotonic))
        {
            return SWMontonicGrain.GetMaxActiveWorkers().ToString();
        }
        else if (benchmarkCase.Descriptor.WorkloadMethod.Name == nameof(StatelessWorkerBenchmark.Adaptive))
        {
            return SWAdaptiveGrain.GetMaxActiveWorkers().ToString();
        }
        return "N/A";
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;
}

public class AvgWorkersColumn : IColumn
{
    public string Id => "AvgWorkers";
    public string ColumnName => "Average Workers";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 11;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Average number of active workers during the benchmark";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        return GetValue(summary, benchmarkCase, SummaryStyle.Default);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        if (benchmarkCase.Descriptor.WorkloadMethod.Name == nameof(StatelessWorkerBenchmark.Monotonic))
        {
            return SWMontonicGrain.GetAverageActiveWorkers().ToString("F2");
        }
        else if (benchmarkCase.Descriptor.WorkloadMethod.Name == nameof(StatelessWorkerBenchmark.Adaptive))
        {
            return SWAdaptiveGrain.GetAverageActiveWorkers().ToString("F2");
        }
        return "N/A";
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;
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

public abstract class BaseGrain<T> : Grain, IProcessorGrain
    where T : BaseGrain<T>
{
    // Static fields are unique for each closed generic type.
    // e.g., BaseGrain<SWMontonicGrain> and BaseGrain<SWAdaptiveGrain>

    private static int _activeWorkers = 0;
    private static int _maxActiveWorkers = 0;
    private static long _totalWorkerSeconds = 0; // Total worker-seconds accumulated
    private static long _lastUpdateTicks = 0;
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
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

    public Task Process() => Task.Delay(Random.Shared.Next(1, 3) * 1000);

    public static int GetActiveWorkers() => Volatile.Read(ref _activeWorkers);
    public static int GetMaxActiveWorkers() => Volatile.Read(ref _maxActiveWorkers);

    public static double GetAverageActiveWorkers()
    {
        var elapsedSeconds = Watch.Elapsed.TotalSeconds;
        return elapsedSeconds == 0 ? 0 : Interlocked.Read(ref _totalWorkerSeconds) / elapsedSeconds;
    }

    private static void UpdateStats()
    {
        var currentWorkers = Interlocked.CompareExchange(ref _activeWorkers, 0, 0);

        // Update max workers
        int oldMax;
        do
        {
            oldMax = Volatile.Read(ref _maxActiveWorkers);
            if (currentWorkers <= oldMax)
            {
                break;
            }

        } while (Interlocked.CompareExchange(ref _maxActiveWorkers, currentWorkers, oldMax) != oldMax);

        // Update total worker-seconds
        long elapsedTicks = Watch.Elapsed.Ticks;
        long lastUpdateTicks = Interlocked.Exchange(ref _lastUpdateTicks, elapsedTicks);
        double elapsedSinceLastUpdate = (elapsedTicks - lastUpdateTicks) / (double)Stopwatch.Frequency;

        Interlocked.Add(ref _totalWorkerSeconds, (long)(currentWorkers * elapsedSinceLastUpdate));
    }
}