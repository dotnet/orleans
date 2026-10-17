using System.Buffers;
using System.Collections.Immutable;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the load balancer's planning, its rounds against embedded partitions, and the balancing of partitions across hosts.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalBalancerTests
{
    private static readonly PartitionId P0 = GarnetTestPartitions.Id(0);
    private static readonly PartitionId P1 = GarnetTestPartitions.Id(1);
    private static readonly PartitionId P2 = GarnetTestPartitions.Id(2);
    private static readonly GarnetJournalBalancerSettings Settings = new(Threshold: 1.25, MinimumLoad: 0, MaxMoveFraction: 0.25);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Plan_BalancedOrIdlePartitions_MovesNothing()
    {
        var owners = Owners(64, P0, P1);
        Assert.Empty(GarnetJournalBalancer.Plan(owners, Fill(64, 10), new long[64], [P0, P1], Settings));
        Assert.Empty(GarnetJournalBalancer.Plan(owners, Fill(64, 0), new long[64], [P0, P1], Settings));

        // Below the minimum load, imbalance is ignored.
        var load = Fill(64, 0);
        load[0] = 50;
        Assert.Empty(GarnetJournalBalancer.Plan(owners, load, new long[64], [P0, P1], Settings with { MinimumLoad = 100 }));
    }

    [Fact]
    public void Plan_MovesLoadFromTheHottestPartition_WithinTheBudget()
    {
        // P0 serves all the load; P2 owns no buckets.
        var owners = Owners(64, P0, P1);
        var load = new double[64];
        for (var bucket = 0; bucket < 32; bucket++)
        {
            load[bucket] = 10;
        }

        var moves = GarnetJournalBalancer.Plan(owners, load, new long[64], [P0, P1, P2], Settings);
        Assert.NotEmpty(moves);
        Assert.InRange(moves.Count, 1, 16);
        Assert.All(moves, move => Assert.Equal(P0, owners[move.Bucket]));

        // Moved buckets form runs at the ends of P0's range, so ranges stay few.
        var after = Apply(owners, moves);
        var loads = new[] { P0, P1, P2 }.Select(partition => Enumerable.Range(0, 64).Where(bucket => after[bucket] == partition).Sum(bucket => load[bucket])).ToArray();
        Assert.True(loads.Max() < 320, $"Loads after balancing: {string.Join(", ", loads)}");
        Assert.InRange(Runs(after), 2, 5);
    }

    [Fact]
    public void Plan_PrefersRunsWithFewerJournals()
    {
        // P0's two ends carry equal load; the start holds many journals, so the end moves.
        var owners = Owners(16, P0, P1);
        var load = new double[16];
        load[0] = 10;
        load[7] = 10;
        load[3] = 10;
        var journals = new long[16];
        journals[0] = 1000;
        var moves = GarnetJournalBalancer.Plan(owners, load, journals, [P0, P1], Settings);
        Assert.Contains(moves, move => move.Bucket == 7);
        Assert.DoesNotContain(moves, move => move.Bucket == 0);
    }

    [Fact]
    public void Plan_DoesNotSplitAHotBucket()
    {
        // One bucket holds nearly all the load: moving it would only move the imbalance.
        var owners = Owners(16, P0, P1);
        var load = Fill(16, 0);
        load[3] = 100;
        Assert.Empty(GarnetJournalBalancer.Plan(owners, load, new long[16], [P0, P1], Settings));
    }

    [Fact]
    public void Plan_MergesColdRangesIntoANeighbor()
    {
        // P2 owns one cold bucket inside P1's range; P0 is overloaded, so the round also merges the island.
        var owners = Owners(128, P0, P1);
        owners[100] = P2;
        var load = new double[128];
        for (var bucket = 0; bucket < 64; bucket++)
        {
            load[bucket] = 10;
        }

        var moves = GarnetJournalBalancer.Plan(owners, load, new long[128], [P0, P1, P2], Settings);
        Assert.Contains((100, P1), moves);
    }

    [Fact]
    public async Task Balancer_MovesLoadAndPreservesJournals()
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(2, 16, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var balancer = new GarnetJournalBalancer(router, coordinator, Settings with { MaxMoveFraction = 0.5 });
        var ids = Enumerable.Range(0, 16).SelectMany(bucket => partitions.JournalsInBucket(bucket, "balance").Take(2)).ToArray();

        // The first round only samples.
        Assert.Empty(await balancer.RunOnceAsync(CancellationToken));
        await Parallel.ForEachAsync(ids, CancellationToken, async (id, token) =>
        {
            var storage = new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0);
            for (var i = 0; i < 3; i++)
            {
                await storage.AppendAsync(new ReadOnlySequence<byte>([(byte)i]), token);
            }
        });

        var moves = await balancer.RunOnceAsync(CancellationToken);
        Assert.NotEmpty(moves);
        Assert.All(moves, move => Assert.Equal(P1, move.Destination));
        var view = await partitions.ReadViewAsync();
        Assert.Contains(view.Map.Ranges, range => range.Owner == P1);

        // A second balancer finds the map busy and commits nothing.
        var duplicate = new GarnetJournalBalancer(router, coordinator, Settings);
        Assert.Empty(await duplicate.RunOnceAsync(CancellationToken));

        await GarnetTestWaits.BoundedAsync(
            coordinator.CompleteAllAsync(CancellationToken),
            "Completing the balancer's migrations",
            async () => GarnetTestWaits.DescribeMap(await partitions.ReadViewAsync()));
        foreach (var id in ids)
        {
            var consumer = new CapturingConsumer();
            await new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0).ReadAsync(consumer, CancellationToken);
            Assert.Equal([0, 1, 2], consumer.Bytes);
        }
    }

    [Fact]
    public void Rate_FirstSampleAtAnOwner_OnlySetsTheBaseline()
    {
        // The bucket moved P0 → P1 → P0; P0's counter kept its earlier tenure's 300,000 operations, and the previous
        // sample saw the bucket at P1.
        var previous = new Dictionary<(PartitionId, int), long> { [(P1, 5)] = 7 };
        Assert.Equal(0, GarnetJournalBalancer.Rate(previous, P0, 5, 300_000, 30));

        // With a baseline at the same owner, the rate is the difference; an owner which restarted counts from zero.
        previous[(P0, 5)] = 300_000;
        Assert.Equal(10, GarnetJournalBalancer.Rate(previous, P0, 5, 300_300, 30));
        Assert.Equal(1, GarnetJournalBalancer.Rate(previous, P0, 5, 30, 30));
    }

    [Fact]
    public async Task Balancer_IgnoresAnEarlierTenuresCount_AfterBucketsMoveAwayAndBack()
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(2, 16, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var balancer = new GarnetJournalBalancer(router, coordinator, Settings with { MaxMoveFraction = 0.5 });
        int[] buckets = [0, 1, 2, 3];

        // Traffic at P0, then the buckets move to P1, where the balancer takes its first sample, and back to P0.
        foreach (var id in buckets.SelectMany(bucket => partitions.JournalsInBucket(bucket, "return").Take(2)))
        {
            var storage = new GarnetJournalStorage(router, id, "json", compactionThresholdBytes: 0);
            for (var i = 0; i < 5; i++)
            {
                await storage.AppendAsync(new ReadOnlySequence<byte>([(byte)i]), CancellationToken);
            }
        }

        await MoveAsync(P1);
        Assert.Empty(await balancer.RunOnceAsync(CancellationToken));
        await MoveAsync(P0);

        // No operation has run since the first sample, so P0's retained counts are not load, and nothing moves.
        Assert.Empty(await balancer.RunOnceAsync(CancellationToken));

        async Task MoveAsync(PartitionId destination)
        {
            var result = await coordinator.TryMoveAsync([.. buckets.Select(bucket => (bucket, destination))], requireIdle: true, CancellationToken);
            Assert.Equal(MoveStatus.Committed, result.Status);
            await GarnetTestWaits.BoundedAsync(
                coordinator.CompleteAllAsync(CancellationToken),
                $"Completing the moves to {destination}",
                async () => GarnetTestWaits.DescribeMap(await partitions.ReadViewAsync()));
        }
    }

    [Fact]
    public async Task Hosting_MovesOnePartitionAtATimeToTheLeastLoadedHost()
    {
        var store = new InMemoryRangeMetaStore();
        var map = RangeMap.CreateUniform(
            16,
            Enumerable.Range(0, 4).Select(index => KeyValuePair.Create(GarnetTestPartitions.Id(index), new PartitionHosting("a", 1, $"p{index}", Endpoint: $"a:{index}"))));
        await store.TryWriteAsync(map, 0, CancellationToken);
        var coordinator = new GarnetJournalHostingCoordinator(store);

        Assert.Null(await coordinator.TryBalanceAsync(["a"], CancellationToken));
        var moved = await coordinator.TryBalanceAsync(["a", "b"], CancellationToken);
        Assert.NotNull(moved);
        var view = (await store.ReadAsync(CancellationToken))!;
        Assert.Equal(new PartitionHosting("b", 2, moved.Value.Value), view.Map.Partitions[moved.Value]);

        // Until the moved partition serves at its new host, no other moves.
        Assert.Null(await coordinator.TryBalanceAsync(["a", "b"], CancellationToken));
        await Publish(store, moved.Value);
        Assert.NotNull(await coordinator.TryBalanceAsync(["a", "b"], CancellationToken));
        view = (await store.ReadAsync(CancellationToken))!;
        await Publish(store, view.Map.Partitions.First(entry => entry.Value.Endpoint is null).Key);

        // Two partitions each: balanced.
        Assert.Null(await coordinator.TryBalanceAsync(["a", "b"], CancellationToken));
        view = (await store.ReadAsync(CancellationToken))!;
        Assert.Equal(2, view.Map.Partitions.Values.Count(hosting => hosting.Host == "b"));

        static async Task Publish(InMemoryRangeMetaStore store, PartitionId partition)
        {
            var view = (await store.ReadAsync(CancellationToken))!;
            var hosting = view.Map.Partitions[partition];
            await store.TryWriteAsync(view.Map with { Partitions = view.Map.Partitions.SetItem(partition, hosting with { Endpoint = $"{hosting.Host}:{partition}" }) }, view.Version, CancellationToken);
        }
    }

    [Fact]
    public async Task Hosting_DoesNotMoveAPartitionWhenAnotherMoveIntervenes()
    {
        var store = new InterveningMetaStore();
        var map = RangeMap.CreateUniform(
            16,
            Enumerable.Range(0, 4).Select(index => KeyValuePair.Create(GarnetTestPartitions.Id(index), new PartitionHosting("a", 1, $"p{index}", Endpoint: $"a:{index}"))));
        await store.TryWriteAsync(map, 0, CancellationToken);

        // Between this coordinator's read and its write, another coordinator, with another membership snapshot, moves a
        // partition to host "c". This coordinator's plan was made while every partition served, so it must not commit.
        var other = GarnetTestPartitions.Id(3);
        store.AfterNextRead = async () =>
        {
            var view = (await store.Inner.ReadAsync(CancellationToken))!;
            var hosting = view.Map.Partitions[other];
            await store.Inner.TryWriteAsync(
                view.Map with { Partitions = view.Map.Partitions.SetItem(other, hosting with { Host = "c", Epoch = hosting.Epoch + 1, Endpoint = null }) },
                view.Version,
                CancellationToken);
        };

        Assert.Null(await new GarnetJournalHostingCoordinator(store).TryBalanceAsync(["a", "b"], CancellationToken));
        var after = (await store.ReadAsync(CancellationToken))!;
        Assert.Single(after.Map.Partitions.Values, hosting => hosting.Endpoint is null);
        Assert.DoesNotContain(after.Map.Partitions.Values, hosting => hosting.Host == "b");
    }

    [Fact]
    public async Task LoadReport_RefusesRangesBeyondItsBudget()
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(1, 16, owners: 1, CancellationToken);
        var database = partitions.Database(P0);

        async Task<int> CountAsync(params string[] args) => ((RedisResult[])(await database.ExecuteAsync(GarnetJournalProtocol.Load, args))!).Length;

        Assert.Equal(1 + 2 * 4, await CountAsync("0", "3"));

        // Ranges whose lengths overflow 32 bits, or which reach int.MaxValue, are measured in 64 bits and bounded.
        Assert.Equal(1, await CountAsync("0", "2147483647"));
        Assert.Equal(1 + 2, await CountAsync("2147483647", "2147483647"));
        Assert.Equal(1 + 2 * 4, await CountAsync("0", "3", "0", "2147483647"));
        Assert.Equal(1 + 2 * 4, await CountAsync("0", "3", "5", "4"));
    }

    /// <summary>A MetaStore which runs a write of another coordinator after returning its next read.</summary>
    private sealed class InterveningMetaStore : IRangeMetaStore
    {
        public InMemoryRangeMetaStore Inner { get; } = new();

        public Func<Task>? AfterNextRead { get; set; }

        public async ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            var view = await Inner.ReadAsync(cancellationToken);
            if (AfterNextRead is { } intervene)
            {
                AfterNextRead = null;
                await intervene();
            }

            return view;
        }

        public ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken)
            => Inner.TryWriteAsync(next, expectedVersion, cancellationToken);
    }

    private static PartitionId[] Owners(int bucketCount, params PartitionId[] owners)
        => Enumerable.Range(0, bucketCount).Select(bucket => owners[bucket * owners.Length / bucketCount]).ToArray();

    private static double[] Fill(int count, double value) => Enumerable.Repeat(value, count).ToArray();

    private static PartitionId[] Apply(PartitionId[] owners, IEnumerable<(int Bucket, PartitionId Destination)> moves)
    {
        var result = (PartitionId[])owners.Clone();
        foreach (var (bucket, destination) in moves)
        {
            result[bucket] = destination;
        }

        return result;
    }

    private static int Runs(PartitionId[] owners) => 1 + Enumerable.Range(1, owners.Length - 1).Count(bucket => owners[bucket] != owners[bucket - 1]);
}
