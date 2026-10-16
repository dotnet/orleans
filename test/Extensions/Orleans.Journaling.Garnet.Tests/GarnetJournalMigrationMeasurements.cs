using System.Buffers;
using System.Diagnostics;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Measures migration costs on embedded partitions on local disk: the cutover pause seen by writers, the overhead of
/// a journal's first access after its bucket moves (a pull), and the consolidation rate.
/// </summary>
[TestCategory("Performance")]
public sealed class GarnetJournalMigrationMeasurements
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(16, 128)]
    public async Task MeasureCutoverPause(int writers, int payloadSize)
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(2, 16, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(partitions.CreateRouter());
        var journals = partitions.JournalsInBucket(3, "cutover").Take(writers).ToArray();
        var payload = new ReadOnlySequence<byte>(new byte[payloadSize]);
        var samples = new List<(long Start, long End)>();
        using var stop = new CancellationTokenSource();
        var clients = journals.Select(journal => Task.Run(async () =>
        {
            var storage = new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0);
            while (!stop.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();
                await storage.AppendAsync(payload, CancellationToken.None);
                var end = Stopwatch.GetTimestamp();
                lock (samples)
                {
                    samples.Add((start, end));
                }
            }
        })).ToArray();

        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
        var commit = Stopwatch.GetTimestamp();
        await coordinator.TryMoveAsync([3], GarnetTestPartitions.Id(1), CancellationToken);
        while (new RangeMapBuckets((await partitions.ReadViewAsync()).Map).GetLatestMigration(3)?.Phase < MigrationPhase.Opened)
        {
            await coordinator.AdvanceAsync(filter: null, CancellationToken);
        }

        var opened = Stopwatch.GetElapsedTime(commit).TotalMilliseconds;
        await coordinator.CompleteAllAsync(CancellationToken);
        var completedAt = Stopwatch.GetTimestamp();
        var complete = Stopwatch.GetElapsedTime(commit, completedAt).TotalMilliseconds;
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
        stop.Cancel();
        await Task.WhenAll(clients);

        // The baseline holds appends which completed before the commit; the migration's samples are every append which
        // overlapped the interval from the commit to the source's reclamation, including those blocked across cutover.
        var before = samples.Where(sample => sample.End < commit).Select(Latency).Order().ToArray();
        var during = samples.Where(sample => sample.Start <= completedAt && sample.End >= commit).Select(Latency).Order().ToArray();
        Report(
            $"cutover writers={writers} payload={payloadSize}B: commit->opened={opened:F1}ms commit->complete={complete:F1}ms; "
            + $"before p50={Percentile(before, 0.5):F2}ms p99={Percentile(before, 0.99):F2}ms max={before[^1]:F2}ms; "
            + $"during p50={Percentile(during, 0.5):F2}ms p99={Percentile(during, 0.99):F2}ms max={during[^1]:F2}ms (n={during.Length})");
    }

    [Theory]
    [InlineData(500, 1024)]
    public async Task MeasurePullOverhead(int journals, int journalSize)
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(2, 16, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = partitions.JournalsInBucket(5, "pull").Take(journals).ToArray();
        await Parallel.ForEachAsync(ids, CancellationToken, async (id, token) =>
            await new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0).AppendAsync(new ReadOnlySequence<byte>(new byte[journalSize]), token));

        var baseline = await ReadAllAsync(router, ids[..100]);
        await coordinator.TryMoveAsync([5], GarnetTestPartitions.Id(1), CancellationToken);
        while (new RangeMapBuckets((await partitions.ReadViewAsync()).Map).GetLatestMigration(5)?.Phase < MigrationPhase.Opened)
        {
            await coordinator.AdvanceAsync(filter: null, CancellationToken);
        }

        var first = await ReadAllAsync(router, ids);
        var second = await ReadAllAsync(router, ids);
        Report(
            $"pull journals={journals} size={journalSize}B: before move p50={Percentile(baseline, 0.5):F3}ms p99={Percentile(baseline, 0.99):F3}ms; "
            + $"first access p50={Percentile(first, 0.5):F3}ms p99={Percentile(first, 0.99):F3}ms; "
            + $"second access p50={Percentile(second, 0.5):F3}ms p99={Percentile(second, 0.99):F3}ms");
    }

    [Theory]
    [InlineData(5000, 1024)]
    public async Task MeasureConsolidationRate(int journals, int journalSize)
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(2, 16, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = partitions.JournalsInBucket(7, "consolidate").Take(journals).ToArray();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = CancellationToken }, async (id, token) =>
            await new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0).AppendAsync(new ReadOnlySequence<byte>(new byte[journalSize]), token));

        await coordinator.TryMoveAsync([7], GarnetTestPartitions.Id(1), CancellationToken);
        while (new RangeMapBuckets((await partitions.ReadViewAsync()).Map).GetLatestMigration(7)?.Phase < MigrationPhase.Opened)
        {
            await coordinator.AdvanceAsync(filter: null, CancellationToken);
        }

        var start = Stopwatch.GetTimestamp();
        var (result, installed) = await router.Migrator.ConsolidateAsync(7, GarnetTestPartitions.Id(1), CancellationToken);
        var consolidation = Stopwatch.GetElapsedTime(start);
        Assert.Equal(ConsolidationResult.Done, result);
        start = Stopwatch.GetTimestamp();
        await coordinator.CompleteAllAsync(CancellationToken);
        var reclamation = Stopwatch.GetElapsedTime(start);
        Report(
            $"consolidation journals={journals} size={journalSize}B: installed={installed} in {consolidation.TotalMilliseconds:F0}ms "
            + $"({installed / consolidation.TotalSeconds:F0} journals/s, {installed * (double)journalSize / consolidation.TotalSeconds / (1 << 20):F1} MiB/s); "
            + $"completion and reclamation {reclamation.TotalMilliseconds:F0}ms");
    }

    private static async Task<double[]> ReadAllAsync(GarnetJournalRouter router, JournalId[] ids)
    {
        var latencies = new double[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await new GarnetJournalStorage(router, ids[i], "json", compactionThresholdBytes: 0).ReadAsync(new CapturingConsumer(), CancellationToken);
            latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(latencies);
        return latencies;
    }

    private static void Report(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        TestContext.Current.SendDiagnosticMessage(message);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "garnet-migration-measurements.txt"), message + Environment.NewLine);
    }

    private static double Latency((long Start, long End) sample) => Stopwatch.GetElapsedTime(sample.Start, sample.End).TotalMilliseconds;

    private static double Percentile(double[] sorted, double percentile)
        => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
}
