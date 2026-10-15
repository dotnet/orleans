using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests routing over several partitions: bucket ownership, view refresh on <c>WrongPartition</c>, ordered view
/// application, and retries while partitions are unavailable.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalRouterTests : IAsyncLifetime
{
    private const int BucketCount = 16;
    private static readonly PartitionId P0 = new("p0");
    private static readonly PartitionId P1 = new("p1");
    private static readonly PartitionId P2 = new("p2");

    private readonly string _directory = GarnetPartitionFixture.CreateTemporaryDirectory();
    private readonly Dictionary<PartitionId, GarnetJournalPartitionHost> _hosts = [];
    private readonly Dictionary<PartitionId, ConnectionMultiplexer> _admin = [];
    private readonly List<GarnetJournalRouter> _routers = [];
    private InMemoryRangeMetaStore _store = null!;
    private string _mapId = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        foreach (var id in new[] { P0, P1, P2 })
        {
            var host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings { DataDirectory = Path.Combine(_directory, id.Value), LogMemorySize = "64m", IndexMemorySize = "1m" },
                loggerFactory: null);
            _hosts.Add(id, host);
            _admin.Add(id, await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { host.EndPoint }, Protocol = RedisProtocol.Resp2 }));
        }

        // p0 and p1 split the buckets; p2 owns none yet.
        var map = RangeMap.CreateUniform(BucketCount, [Hosting(P0), Hosting(P1)]);
        map = map with { Partitions = map.Partitions.Add(P2, Hosting(P2).Value) };
        _mapId = map.Id;
        _store = new InMemoryRangeMetaStore();
        await _store.TryWriteAsync(map, 0, CancellationToken);
        await GarnetJournalPartitionManager.InitializeAsync(_hosts[P0].EndPoint, P0, map, CancellationToken);
        await GarnetJournalPartitionManager.InitializeAsync(_hosts[P1].EndPoint, P1, map, CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var router in _routers)
        {
            await router.DisposeAsync();
        }

        foreach (var connection in _admin.Values)
        {
            await connection.DisposeAsync();
        }

        foreach (var host in _hosts.Values)
        {
            host.Dispose();
        }

        GarnetPartitionFixture.TryDeleteDirectory(_directory);
    }

    [Fact]
    public async Task Operations_RunOnTheOwnerOfTheirBucket()
    {
        var router = await CreateRouterAsync();
        var view = router.View.Map;
        var journals = Enumerable.Range(0, 64).Select(index => CreateStorage(router, $"owner-{index}")).ToArray();
        foreach (var storage in journals)
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        }

        Assert.Contains(journals, storage => view.GetOwner(storage.Bucket) == P0);
        Assert.Contains(journals, storage => view.GetOwner(storage.Bucket) == P1);
        foreach (var storage in journals)
        {
            var owner = view.GetOwner(storage.Bucket);
            foreach (var (id, connection) in _admin)
            {
                Assert.Equal(id == owner, await connection.GetDatabase().KeyExistsAsync(storage.DataKey));
            }
        }
    }

    [Fact]
    public async Task WrongPartition_RefreshesTheViewAndRetriesAtTheNewOwner()
    {
        var stale = await CreateRouterAsync();
        var storage = CreateStorage(stale, "moved");
        var bucket = storage.Bucket;
        var source = stale.View.Map.GetOwner(bucket);
        var destination = source == P0 ? P1 : P0;

        // Move the bucket, which holds no journals, as a handover would: commit the new view, seal the bucket in its
        // source at that version, then open it in its destination.
        var current = (await _store.ReadAsync(CancellationToken))!;
        var moved = await _store.TryWriteAsync(GarnetTestRouting.AssignBucket(current.Map, bucket, destination, current.Version + 1), current.Version, CancellationToken);
        Assert.True(moved.Written);
        var version = moved.Current!.Version;
        Assert.Equal("1", await TransitionAsync(source, bucket, "seal", version));
        Assert.Equal("1", await TransitionAsync(destination, bucket, "open", version));

        // The stale router sends the append to the source, which reports the seal; the router refreshes and retries.
        Assert.Equal(version - 1, stale.View.Version);
        await storage.AppendAsync(new ReadOnlySequence<byte>([7]), CancellationToken);
        Assert.Equal(version, stale.View.Version);
        Assert.True(await _admin[destination].GetDatabase().KeyExistsAsync(storage.DataKey));
        Assert.False(await _admin[source].GetDatabase().KeyExistsAsync(storage.DataKey));

        var consumer = new CapturingConsumer();
        await CreateStorage(await CreateRouterAsync(), "moved").ReadAsync(consumer, CancellationToken);
        Assert.Equal([7], consumer.Bytes);
    }

    [Fact]
    public async Task Views_AreAppliedInVersionOrder()
    {
        var store = new ReplayingMetaStore(_store);
        var initial = (await _store.ReadAsync(CancellationToken))!;
        var router = new GarnetJournalRouter(store, initial, TimeSpan.FromSeconds(5));
        _routers.Add(router);
        var newer = (await _store.TryWriteAsync(initial.Map with { Partitions = initial.Map.Partitions.SetItem(P2, initial.Map.Partitions[P2] with { Epoch = 1 }) }, initial.Version, CancellationToken)).Current!;

        Assert.True(router.TryApplyView(newer));
        Assert.False(router.TryApplyView(newer));
        Assert.False(router.TryApplyView(initial));
        Assert.Same(newer, router.View);

        // A MetaStore read which returns an older view does not regress the router.
        store.Replay = initial;
        Assert.Same(newer, await router.RefreshAsync(CancellationToken));

        Assert.Throws<InvalidOperationException>(() => router.TryApplyView(new RangeMapSnapshot(newer.Version + 1, newer.Map with { BucketCount = BucketCount * 2, Ranges = [new(0, BucketCount * 2 - 1, P0)] })));
        Assert.Throws<InvalidOperationException>(() => router.TryApplyView(new RangeMapSnapshot(newer.Version + 1, newer.Map with { Ranges = [] })));
        Assert.Same(newer, router.View);
    }

    [Fact]
    public async Task OwnerWhichHasNotOpenedItsBucket_IsRetriedUntilItOpens()
    {
        var router = await CreateRouterAsync();
        var storage = CreateStorage(router, "late-open");
        var bucket = storage.Bucket;
        var current = (await _store.ReadAsync(CancellationToken))!;
        var source = current.Map.GetOwner(bucket);

        // Assign the empty bucket to p2, which has not opened it. Its WrongPartition reply reports a version no newer
        // than the router's view, so the router backs off rather than refreshing in a loop.
        var assigned = (await _store.TryWriteAsync(GarnetTestRouting.AssignBucket(current.Map, bucket, P2, current.Version + 1), current.Version, CancellationToken)).Current!;
        Assert.Equal("1", await TransitionAsync(source, bucket, "seal", assigned.Version));
        await router.RefreshAsync(CancellationToken);

        var append = storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken).AsTask();
        await Task.Delay(300, CancellationToken);
        Assert.False(append.IsCompleted);
        Assert.Equal("1", await TransitionAsync(P2, bucket, "open", assigned.Version));
        await append;
        Assert.True(await _admin[P2].GetDatabase().KeyExistsAsync(storage.DataKey));
    }

    [Fact]
    public async Task UnpublishedEndpoint_IsRetriedUntilItsHostPublishesIt()
    {
        await CreateRouterAsync();
        var current = (await _store.ReadAsync(CancellationToken))!;
        var unpublished = current.Map with { Partitions = current.Map.Partitions.SetItem(P1, current.Map.Partitions[P1] with { Endpoint = null }) };
        var view = (await _store.TryWriteAsync(unpublished, current.Version, CancellationToken)).Current!;
        var router = new GarnetJournalRouter(_store, view, TimeSpan.FromSeconds(10));
        _routers.Add(router);
        var storage = Enumerable.Range(0, 100).Select(index => CreateStorage(router, $"unpublished-{index}")).First(storage => view.Map.GetOwner(storage.Bucket) == P1);

        var append = storage.AppendAsync(new ReadOnlySequence<byte>([5]), CancellationToken).AsTask();
        await Task.Delay(300, CancellationToken);
        Assert.False(append.IsCompleted);
        Assert.True((await _store.TryWriteAsync(current.Map, view.Version, CancellationToken)).Written);
        await append;
        Assert.True(await _admin[P1].GetDatabase().KeyExistsAsync(storage.DataKey));
    }

    [Fact]
    public async Task UnreachablePartition_TimesOut()
    {
        await CreateRouterAsync();
        var current = (await _store.ReadAsync(CancellationToken))!;
        var unreachable = current.Map with { Partitions = current.Map.Partitions.SetItem(P1, current.Map.Partitions[P1] with { Endpoint = $"127.0.0.1:{GetUnusedPort()}" }) };
        var view = (await _store.TryWriteAsync(unreachable, current.Version, CancellationToken)).Current!;
        var router = new GarnetJournalRouter(_store, view, TimeSpan.FromSeconds(1));
        _routers.Add(router);
        var storage = Enumerable.Range(0, 100).Select(index => CreateStorage(router, $"unreachable-{index}")).First(storage => view.Map.GetOwner(storage.Bucket) == P1);

        await Assert.ThrowsAsync<TimeoutException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([5]), CancellationToken).AsTask());
    }

    [Fact]
    public async Task StalledMetaStoreRefresh_IsBoundedByOperationTimeout()
    {
        // The owner of the journal's bucket has no published endpoint, so the router refreshes from a MetaStore whose
        // reads never complete.
        await CreateRouterAsync();
        var current = (await _store.ReadAsync(CancellationToken))!;
        var unpublished = current.Map with { Partitions = current.Map.Partitions.SetItem(P1, current.Map.Partitions[P1] with { Endpoint = null }) };
        var view = (await _store.TryWriteAsync(unpublished, current.Version, CancellationToken)).Current!;
        var store = new StalledMetaStore();
        var router = new GarnetJournalRouter(store, view, TimeSpan.FromMilliseconds(500));
        _routers.Add(router);
        var storages = Enumerable.Range(0, 100).Select(index => CreateStorage(router, $"stalled-{index}")).Where(storage => view.Map.GetOwner(storage.Bucket) == P1).Take(2).ToArray();

        foreach (var storage in storages)
        {
            var append = storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken).AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => append.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken));
        }

        // Both operations waited for one shared read, which their deadlines did not cancel.
        Assert.Equal(1, store.Reads);
        Assert.False(store.ReadCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Disposal_StopsRetriesWithoutReopeningConnections()
    {
        var router = await CreateRouterAsync();
        var storage = CreateStorage(router, "disposal");
        var bucket = storage.Bucket;

        // Assign the bucket to p2, which never opens it, so the append retries until the router is disposed.
        var current = (await _store.ReadAsync(CancellationToken))!;
        Assert.True((await _store.TryWriteAsync(GarnetTestRouting.AssignBucket(current.Map, bucket, P2, current.Version + 1), current.Version, CancellationToken)).Written);
        await router.RefreshAsync(CancellationToken);
        var append = storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken).AsTask();
        await Task.Delay(300, CancellationToken);
        Assert.False(append.IsCompleted);
        Assert.True(router.ConnectionCount > 0);

        await router.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => append.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => CreateStorage(router, "after-disposal").AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        Assert.Equal(0, router.ConnectionCount);
    }

    [Fact]
    public async Task PartitionOfAnotherMap_RejectsOperations()
    {
        // A router of another map whose endpoints, for example stale ones, reach this map's partitions.
        var own = (await CreateRouterAsync()).View;
        var foreignStore = new InMemoryRangeMetaStore();
        var foreign = (await foreignStore.TryWriteAsync(own.Map with { Id = RangeMap.CreateId() }, 0, CancellationToken)).Current!;
        var router = new GarnetJournalRouter(foreignStore, foreign, TimeSpan.FromMilliseconds(500));
        _routers.Add(router);
        var storage = CreateStorage(router, "foreign");

        await Assert.ThrowsAsync<TimeoutException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken).AsTask());
        foreach (var connection in _admin.Values)
        {
            Assert.False(await connection.GetDatabase().KeyExistsAsync(storage.DataKey));
        }
    }

    private static KeyValuePair<PartitionId, PartitionHosting> Hosting(PartitionId id) => new(id, new PartitionHosting("test", 0, id.Value));

    private async Task<GarnetJournalRouter> CreateRouterAsync()
    {
        // Publish every partition's endpoint, as the hosting silos would.
        var current = (await _store.ReadAsync(CancellationToken))!;
        var partitions = current.Map.Partitions;
        foreach (var (id, host) in _hosts)
        {
            partitions = partitions.SetItem(id, partitions[id] with { Endpoint = GarnetJournalPartitionManager.FormatEndpoint(host.EndPoint) });
        }

        if (partitions != current.Map.Partitions)
        {
            current = (await _store.TryWriteAsync(current.Map with { Partitions = partitions }, current.Version, CancellationToken)).Current!;
        }

        var router = new GarnetJournalRouter(_store, current, TimeSpan.FromSeconds(10));
        _routers.Add(router);
        return router;
    }

    private static GarnetJournalStorage CreateStorage(GarnetJournalRouter router, string name)
        => new(router, JournalId.Create("router", name), "json", compactionThresholdBytes: 0);

    private async Task<string?> TransitionAsync(PartitionId partition, int bucket, string operation, long version)
    {
        var result = (RedisResult[])(await _admin[partition].GetDatabase().ExecuteAsync(
            GarnetJournalProtocol.Bucket,
            [(RedisKey)GarnetJournalKeys.GetBucketKey(_mapId, bucket), operation, version.ToString(), GarnetJournalProtocol.NoSource, "1"]))!;
        return (string?)result[0];
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A MetaStore whose reads never complete unless cancelled.</summary>
    private sealed class StalledMetaStore : IRangeMetaStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public CancellationToken ReadCancellation { get; private set; }

        public async ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            ReadCancellation = cancellationToken;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A MetaStore whose reads can be made to return an older snapshot.</summary>
    private sealed class ReplayingMetaStore(IRangeMetaStore inner) : IRangeMetaStore
    {
        public RangeMapSnapshot? Replay { get; set; }

        public async ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken)
            => Replay ?? await inner.ReadAsync(cancellationToken);

        public ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken)
            => inner.TryWriteAsync(next, expectedVersion, cancellationToken);
    }
}
