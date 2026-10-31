using System.Buffers;
using System.Diagnostics;
using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Measures partitions on Azure page blobs: append latency and throughput, and the time to fail a partition over to
/// another host while writers append to it.
/// </summary>
[TestCategory("Performance"), TestCategory("AzureStorage")]
public sealed class GarnetJournalFailoverMeasurements : IAsyncDisposable
{
    private readonly BlobContainerClient _container;
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<GarnetJournalPartitionHost> _hosts = [];

    public GarnetJournalFailoverMeasurements()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        _container = GarnetBlobTestStorage.CreateContainer();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 1000, 128)]
    [InlineData(16, 300, 128)]
    [InlineData(64, 100, 128)]
    public async Task MeasureAppendLatencyOnBlobStorage(int writers, int appendsPerWriter, int payloadSize)
    {
        var (_, host) = await GarnetBlobTestStorage.StartAsync(_container, "p0", epoch: 1, CancellationToken);
        _hosts.Add(host);
        var router = await GarnetBlobTestStorage.CreateRouterAsync(new InMemoryRangeMetaStore(), GarnetBlobTestStorage.CreateMap(), host.EndPoint, TimeSpan.FromSeconds(30), CancellationToken);
        _disposables.Add(router);
        var payload = new ReadOnlySequence<byte>(new byte[payloadSize]);
        var storages = Enumerable.Range(0, writers).Select(index => new GarnetJournalStorage(router, JournalId.Create("latency", index.ToString()), "json", 0)).ToArray();
        foreach (var storage in storages)
        {
            await storage.AppendAsync(payload, CancellationToken);
        }

        var latencies = new double[writers * appendsPerWriter];
        var total = Stopwatch.StartNew();
        await Task.WhenAll(storages.Select((storage, writer) => Task.Run(async () =>
        {
            for (var i = 0; i < appendsPerWriter; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await storage.AppendAsync(payload, CancellationToken);
                latencies[writer * appendsPerWriter + i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }, CancellationToken)));
        total.Stop();

        Array.Sort(latencies);
        Report(
            $"page blobs writers={writers} appends={latencies.Length} payload={payloadSize}B: "
            + $"p50={Percentile(latencies, 0.50):F2}ms p99={Percentile(latencies, 0.99):F2}ms p99.9={Percentile(latencies, 0.999):F2}ms "
            + $"max={latencies[^1]:F2}ms throughput={latencies.Length / total.Elapsed.TotalSeconds:F0}/s");
    }

    /// <summary>
    /// Fails a partition over from one host to another while writers append, and reports the time from the assignment
    /// until the new host serves, and the longest append any writer saw. Membership's detection of a dead host comes
    /// before this in a cluster.
    /// </summary>
    [Theory]
    [InlineData(16, 200)]
    [InlineData(16, 5000)]
    public async Task MeasureFailoverTime(int writers, int preloadedJournals)
    {
        var store = new InMemoryRangeMetaStore();
        var p0 = GarnetTestRouting.P0;
        var map = RangeMap.CreateUniform(16, [KeyValuePair.Create(p0, new PartitionHosting("a", 0, "p0"))]);
        await store.TryWriteAsync(map, 0, CancellationToken);
        var managers = new Dictionary<string, GarnetJournalPartitionManager>();
        foreach (var host in new[] { "a", "b" })
        {
            var options = new GarnetJournalStorageOptions { MetaStore = store, PartitionBlobContainer = _container, LogMemorySize = "64m", IndexMemorySize = "1m" };
            managers[host] = await GarnetJournalPartitionManager.StartAsync(options, host, store, (await store.ReadAsync(CancellationToken))!, loggerFactory: null, CancellationToken);
            _disposables.Add(managers[host]);
        }

        var router = new GarnetJournalRouter(store, (await store.ReadAsync(CancellationToken))!, TimeSpan.FromSeconds(60));
        _disposables.Add(router);
        var kilobyte = new ReadOnlySequence<byte>(new byte[1024]);
        await Parallel.ForEachAsync(Enumerable.Range(0, preloadedJournals), new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = CancellationToken }, async (index, token) =>
            await new GarnetJournalStorage(router, JournalId.Create("preloaded", index.ToString()), "json", 0).AppendAsync(kilobyte, token));

        var payload = new ReadOnlySequence<byte>(new byte[128]);
        var samples = new List<(long Start, double Latency)>();
        using var stop = new CancellationTokenSource();
        var clients = Enumerable.Range(0, writers).Select(index => Task.Run(async () =>
        {
            var storage = new GarnetJournalStorage(router, JournalId.Create("writer", index.ToString()), "json", 0);
            while (!stop.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();
                await storage.AppendAsync(payload, CancellationToken.None);
                lock (samples)
                {
                    samples.Add((start, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
                }
            }
        })).ToArray();

        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
        var assignedAt = Stopwatch.GetTimestamp();
        var epoch = (await store.ReadAsync(CancellationToken))!.Map.Partitions[p0].Epoch;
        Assert.NotNull(await new GarnetJournalHostingCoordinator(store).TryAssignAsync(p0, epoch, "b", CancellationToken));

        // The new host takes the storage over, recovers, and publishes its endpoint; the previous host is a zombie
        // until it observes the change.
        await managers["b"].RefreshAsync(CancellationToken);
        var served = Stopwatch.GetElapsedTime(assignedAt).TotalMilliseconds;
        await managers["a"].RefreshAsync(CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken);
        stop.Cancel();
        await Task.WhenAll(clients);

        var before = samples.Where(sample => sample.Start < assignedAt).Select(sample => sample.Latency).Order().ToArray();
        var spanning = samples.Where(sample => sample.Start >= assignedAt - Stopwatch.Frequency).Select(sample => sample.Latency).Order().ToArray();
        var after = samples.Where(sample => Stopwatch.GetElapsedTime(assignedAt, sample.Start).TotalMilliseconds > served + 1000).Select(sample => sample.Latency).Order().ToArray();
        Report(
            $"failover writers={writers} journals={preloadedJournals + writers}: assignment->served={served:F0}ms; longest append={spanning[^1]:F0}ms; "
            + $"before p50={Percentile(before, 0.5):F2}ms p99={Percentile(before, 0.99):F2}ms; after p50={Percentile(after, 0.5):F2}ms p99={Percentile(after, 0.99):F2}ms");
        Assert.True(managers["b"].IsHosted(p0));
        Assert.False(managers["a"].IsHosted(p0));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        await _container.DeleteIfExistsAsync();
    }

    private static void Report(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        TestContext.Current.SendDiagnosticMessage(message);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "garnet-failover-measurements.txt"), message + Environment.NewLine);
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
}
