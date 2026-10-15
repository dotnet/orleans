using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;
using TestExtensions;
using Xunit.Sdk;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Fails partitions over between hosts on shared blob storage while clients, including duplicate activations, read and
/// write journals and buckets move between partitions; previous hosts keep running as zombies until they observe the
/// change. Checks that every journal's history is linearizable and that every acknowledged write survives.
/// </summary>
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class GarnetJournalFailoverTests : IAsyncDisposable
{
    private readonly BlobContainerClient _container;
    private readonly List<IAsyncDisposable> _disposables = [];

    public GarnetJournalFailoverTests()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        _container = GarnetBlobTestStorage.CreateContainer();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failover_UnderLoad_WithZombieHostsAndMigrations_IsLinearizable()
    {
        const int BucketCount = 16;
        var store = new InMemoryRangeMetaStore();
        var partitions = Enumerable.Range(0, 3).Select(GarnetTestPartitions.Id).ToArray();
        var hosts = Enumerable.Range(0, 4).Select(index => $"h{index}").ToArray();
        var hostings = partitions.Select((id, index) => KeyValuePair.Create(id, new PartitionHosting(hosts[index], 0, id.Value))).ToArray();
        var map = RangeMap.CreateUniform(BucketCount, hostings.Take(2)) with { Partitions = hostings.ToImmutableSortedDictionary() };
        await store.TryWriteAsync(map, 0, CancellationToken);

        // Hosts follow the map only when the test refreshes them, so a host which loses a partition keeps serving it,
        // as a zombie, until a later refresh.
        var managers = new Dictionary<string, GarnetJournalPartitionManager>();
        foreach (var host in hosts)
        {
            managers[host] = await StartManagerAsync(store, host);
        }

        var workload = new JournalWorkload(new LinearizabilityHistory());
        var journals = Enumerable.Range(0, 6).Select(index => JournalId.Create("failover", index.ToString())).ToArray();
        GarnetJournalRouter CreateRouter(int seed)
        {
            var router = new GarnetJournalRouter(store, store.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()!, TimeSpan.FromSeconds(30));
            Register(router);
            var faults = new Random(seed);
            router.DropReply = _ => { lock (faults) { return faults.Next(20) == 0; } };
            return router;
        }

        using var stop = new CancellationTokenSource();
        var clients = journals
            .SelectMany((journal, index) => Enumerable.Range(0, 3).Select(client => Task.Run(() => workload.RunClientAsync(CreateRouter(index * 10 + client), journal, index * 10 + client, stop.Token))))
            .ToList();
        var drivers = Enumerable.Range(0, 2).Select(index => Task.Run(() => DriveAsync(new GarnetJournalMigrationCoordinator(CreateRouter(100 + index)), stop.Token))).ToList();
        var mover = new GarnetJournalMigrationCoordinator(CreateRouter(200));
        var hosting = new GarnetJournalHostingCoordinator(store);
        async Task<string> DescribeAsync()
            => $"{GarnetTestWaits.DescribeMap(await store.ReadAsync(CancellationToken.None))} Served: [{string.Join("; ", managers.Select(entry => $"{entry.Key}: {string.Join(", ", entry.Value.Hosts.Keys)}"))}].";

        var random = new Random(31);
        var failovers = 0;
        var moves = 0;
        const int Rounds = 12;
        for (var round = 0; round < Rounds; round++)
        {
            var view = (await store.ReadAsync(CancellationToken))!;
            var partition = partitions[random.Next(partitions.Length)];
            var current = view.Map.Partitions[partition];
            var others = hosts.Where(host => host != current.Host).ToArray();
            var target = others[random.Next(others.Length)];
            if (await hosting.TryAssignAsync(partition, current.Epoch, target, CancellationToken) is not null)
            {
                failovers++;
            }

            // The new host takes the partition over, fencing the previous host, which observes the change at once or
            // later; meanwhile, clients with stale views reach it.
            await GarnetTestWaits.BoundedAsync(managers[target].RefreshAsync(CancellationToken), $"Host {target} taking {partition} over (round {round})", DescribeAsync);
            foreach (var host in hosts.Where(host => host != target && random.Next(2) == 0))
            {
                await GarnetTestWaits.BoundedAsync(managers[host].RefreshAsync(CancellationToken), $"Host {host} following the map (round {round})", DescribeAsync);
            }

            var buckets = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(BucketCount)).ToArray();
            var move = await GarnetTestWaits.BoundedAsync(mover.TryMoveAsync(buckets, partitions[random.Next(partitions.Length)], CancellationToken), $"Committing a move (round {round})", DescribeAsync);
            if (move.Status == MoveStatus.Committed)
            {
                moves++;
            }

            await Task.Delay(random.Next(200, 800), CancellationToken);
        }

        stop.Cancel();
        await GarnetTestWaits.BoundedAsync(Task.WhenAll(clients.Concat(drivers)), "Clients and coordinators stopping", DescribeAsync);
        foreach (var (host, manager) in managers)
        {
            await GarnetTestWaits.BoundedAsync(manager.RefreshAsync(CancellationToken), $"Host {host} following the final map", DescribeAsync);
        }

        await CompleteMigrationsAsync(new GarnetJournalMigrationCoordinator(CreateRouter(300)), DescribeAsync);

        // Each client's last write is visible to a final read.
        var reader = CreateRouter(400);
        reader.DropReply = null;
        foreach (var journal in journals)
        {
            // An unknown outcome would let the checker skip the read, leaving the recovered state unchecked.
            var lastRead = await GarnetTestWaits.BoundedAsync(
                workload.ReadAsync(new GarnetJournalStorage(reader, journal, "json", compactionThresholdBytes: 0), client: 0, journal),
                $"The final read of {journal}",
                DescribeAsync);
            Assert.True(lastRead is not null, $"The final read of {journal} failed. {await DescribeAsync()}");
        }

        var failures = workload.History.Check();
        TestContext.Current.SendDiagnosticMessage($"Checked {workload.History.Count} operations ({workload.History.UnknownCount} with unknown outcomes) on {journals.Length} journals.");
        if (failures.Count > 0)
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "garnet-failover-linearizability.txt"), failures);
            throw new XunitException($"{failures.Count} journal histories failed the linearizability check:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{failovers} failovers, {moves} moves, {workload.History.Count} operations.");
        Assert.Equal(Rounds, failovers);
        Assert.True(moves >= 3, $"Only {moves} moves were committed.");
        Assert.True(workload.History.Count > 100, $"Only {workload.History.Count} operations were recorded.");

        // Each partition is served by exactly the host the map assigns it to.
        var final = (await store.ReadAsync(CancellationToken))!.Map;
        foreach (var partition in partitions)
        {
            Assert.Equal(new[] { final.Partitions[partition].Host }, managers.Where(entry => entry.Value.IsHosted(partition)).Select(entry => entry.Key));
        }
    }

    [Fact]
    public async Task KilledHostProcess_NewHostRecoversEveryAcknowledgedWrite()
    {
        Assert.SkipWhen(TestDefaultConfiguration.UseAadAuthentication, "The crash host connects with a connection string.");
        var map = GarnetBlobTestStorage.CreateMap();
        var acknowledged = new Dictionary<JournalId, int>();
        var random = new Random(41);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Each cycle's host takes the partition over from the killed one, recovering its writes, and writes more.
            using var process = await CrashHostProcess.StartOnBlobsAsync(TestDefaultConfiguration.DataConnectionString!, _container.Name, "p0", epoch: cycle + 1, CancellationToken);
            var router = await GarnetBlobTestStorage.CreateRouterAsync(new InMemoryRangeMetaStore(), map, process.EndPoint, TimeSpan.FromSeconds(3), CancellationToken);
            Register(router);
            foreach (var (journal, count) in acknowledged)
            {
                var values = (await JournalWorkload.ReadUnrecordedAsync(router, journal)).Values ?? [];
                Assert.Equal(Enumerable.Range(0, values.Count), values);
                Assert.InRange(values.Count, count, count + 1);
            }

            var journals = Enumerable.Range(0, 8).Select(index => JournalId.Create($"killed{cycle}", index.ToString())).ToArray();
            var counts = new int[journals.Length];
            var firstAcknowledged = journals.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var writers = journals.Select((journal, index) => Task.Run(async () =>
            {
                var storage = new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0);
                for (var value = 0; ; value++)
                {
                    try
                    {
                        await storage.AppendAsync(Payload(value), CancellationToken.None);
                        counts[index] = value + 1;
                        firstAcknowledged[index].TrySetResult();
                    }
                    catch (Exception exception) when (exception is TimeoutException or RedisException)
                    {
                        firstAcknowledged[index].TrySetException(exception);
                        return;
                    }
                }
            })).ToArray();

            // Kill only once every writer has an acknowledged append; all of them keep appending through the kill.
            await GarnetTestWaits.BoundedAsync(
                Task.WhenAll(firstAcknowledged.Select(signal => signal.Task)),
                $"Every writer's first append in cycle {cycle}",
                () => Task.FromResult($"Acknowledged appends so far: [{string.Join(", ", counts)}]."),
                TimeSpan.FromSeconds(60));
            await Task.Delay(random.Next(300, 1500), CancellationToken);
            process.Kill();
            await GarnetTestWaits.BoundedAsync(
                Task.WhenAll(writers),
                $"Writers failing after the kill of cycle {cycle}'s host",
                () => Task.FromResult($"Acknowledged appends so far: [{string.Join(", ", counts)}]."),
                TimeSpan.FromSeconds(60));
            for (var i = 0; i < journals.Length; i++)
            {
                Assert.True(counts[i] > 0, $"No append to {journals[i]} was acknowledged before the kill.");
                acknowledged[journals[i]] = counts[i];
            }
        }

        var (_, host) = await GarnetBlobTestStorage.StartAsync(_container, "p0", epoch: 10, CancellationToken);
        using (host)
        {
            var router = await GarnetBlobTestStorage.CreateRouterAsync(new InMemoryRangeMetaStore(), map, host.EndPoint, TimeSpan.FromSeconds(5), CancellationToken);
            Register(router);
            foreach (var (journal, count) in acknowledged)
            {
                var values = (await JournalWorkload.ReadUnrecordedAsync(router, journal)).Values!;
                Assert.Equal(Enumerable.Range(0, values.Count), values);
                Assert.InRange(values.Count, count, count + 1);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable> disposables;
        lock (_disposables)
        {
            disposables = [.. _disposables];
        }

        foreach (var disposable in Enumerable.Reverse(disposables))
        {
            await disposable.DisposeAsync();
        }

        await _container.DeleteIfExistsAsync();
    }

    /// <summary>Registers a resource for disposal; client and driver tasks register routers concurrently.</summary>
    private void Register(IAsyncDisposable disposable)
    {
        lock (_disposables)
        {
            _disposables.Add(disposable);
        }
    }

    private async Task<GarnetJournalPartitionManager> StartManagerAsync(IRangeMetaStore store, string host)
    {
        var options = new GarnetJournalStorageOptions
        {
            MetaStore = store,
            PartitionBlobContainer = _container,
            LogMemorySize = "64m",
            IndexMemorySize = "1m",
        };
        var manager = await GarnetJournalPartitionManager.StartAsync(options, host, store, (await store.ReadAsync(CancellationToken))!, loggerFactory: null, CancellationToken);
        Register(manager);
        return manager;
    }

    private static ReadOnlySequence<byte> Payload(int value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return new(payload);
    }

    private static async Task DriveAsync(GarnetJournalMigrationCoordinator coordinator, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await coordinator.AdvanceAsync(filter: null, stop))
                {
                    await Task.Delay(5, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException)
            {
                // A partition was failing over for a whole operation timeout.
            }
        }
    }

    private static async Task CompleteMigrationsAsync(GarnetJournalMigrationCoordinator coordinator, Func<Task<string>> describe)
    {
        var bound = TimeSpan.FromSeconds(90);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(bound);
        while (true)
        {
            try
            {
                await coordinator.CompleteAllAsync(timeout.Token);
                return;
            }
            catch (TimeoutException) when (!timeout.IsCancellationRequested)
            {
                // A partition was unavailable for a whole operation timeout; continue from the recorded phases.
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !CancellationToken.IsCancellationRequested)
            {
                throw new XunitException($"Migrations did not complete within {bound}. {await describe()}");
            }
        }
    }
}
