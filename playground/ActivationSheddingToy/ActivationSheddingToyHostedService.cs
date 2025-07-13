using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;
using Orleans.Statistics;

internal sealed class ActivationSheddingToyHostedService(IGrainFactory grainFactory, IEnvironmentStatisticsProvider environmentStatisticsProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var i = 0;
        var tasks = new List<Task>();
        var stats = new List<(int GrainCount, long UsageBytes)>();
        while (!stoppingToken.IsCancellationRequested)
        {
            while (tasks.Count < 10_000)
            {
                var grain = grainFactory.GetGrain<IActivationSheddingToyGrain>(++i);
                tasks.Add(grain.Ping());
            }

            await Task.WhenAll(tasks);
            tasks.Clear();

            if (i % 100_000 == 0)
            {
                var activationCount = await grainFactory.GetGrain<IManagementGrain>(0).GetTotalActivationCount();
                var envStats = environmentStatisticsProvider.GetEnvironmentStatistics();
                while (envStats.MemoryUsagePercentage > 80)
                {
                    Console.WriteLine($"Memory usage is high ({envStats.MemoryUsagePercentage:N2}%) with {activationCount} activations.");
                    GC.Collect();
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    envStats = environmentStatisticsProvider.GetEnvironmentStatistics();
                    activationCount = await grainFactory.GetGrain<IManagementGrain>(0).GetTotalActivationCount();
                }

                PrintUsage(i, stats, envStats, activationCount);
            }

            if (stats.Count == 50)
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    var activationCount = await grainFactory.GetGrain<IManagementGrain>(0).GetTotalActivationCount();
                    var envStats = environmentStatisticsProvider.GetEnvironmentStatistics();
                    PrintUsage(i, stats, envStats, activationCount);
                    if (stats.Count == 10)
                    {
                        break;
                    }
                }

                break;
            }
        }

        PlotAndPrintStats(stats);
    }

    private void PrintUsage(int i, List<(int GrainCount, long UsageBytes)> stats, EnvironmentStatistics envStats, int activationCount)
    {
        GC.Collect();
        var usage = GC.GetTotalMemory(forceFullCollection: true);
        stats.Add((i, usage));
        Console.WriteLine($"{i:N0} active grains. {usage:N0}bytes used. {usage / i:N0}bytes/grain (approx)");
        Console.WriteLine($"\t{envStats}");
        Console.WriteLine($"\tActivations: {activationCount}");
    }

    private static void PlotAndPrintStats(List<(int GrainCount, long UsageBytes)> stats)
    {
        if (stats.Count < 2)
        {
            Console.WriteLine("Not enough data points to plot or calculate marginal usage.");
            return;
        }

        // Calculate and print marginal memory usage per grain
        Console.WriteLine("Marginal memory usage per grain (bytes) between points:");
        for (int j = 1; j < stats.Count; j++)
        {
            int deltaGrains = stats[j].GrainCount - stats[j - 1].GrainCount;
            long deltaBytes = stats[j].UsageBytes - stats[j - 1].UsageBytes;
            double marginal = deltaGrains != 0 ? (double)deltaBytes / deltaGrains : double.NaN;
            Console.WriteLine($"{stats[j - 1].GrainCount:N0} -> {stats[j].GrainCount:N0}: {marginal:N2} bytes/grain");
        }
    }
}

internal sealed class ActivationSheddingToyGrain : Grain, IActivationSheddingToyGrain
{
    private byte[] _buffer = new byte[100_000];
    public Task Ping()
    {
        _ = _buffer;
        return Task.CompletedTask;
    }
}

internal interface IActivationSheddingToyGrain : IGrainWithIntegerKey
{
    Task Ping();
}
