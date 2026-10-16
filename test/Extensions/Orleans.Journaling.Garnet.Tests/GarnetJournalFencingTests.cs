using System.Buffers;
using System.Buffers.Binary;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests partitions on shared blob storage: recovery by another host, and fencing of the previous host (D12). Each test
/// keeps the previous host running as a zombie, which has not observed that another host took its partition over.
/// </summary>
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class GarnetJournalFencingTests : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly BlobContainerClient _container;
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<GarnetJournalPartitionHost> _hosts = [];

    public GarnetJournalFencingTests()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        _container = GarnetBlobTestStorage.CreateContainer();
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Partition_RecoversFromBlobStorage_OnAnotherHost()
    {
        var map = GarnetBlobTestStorage.CreateMap();
        var (_, first) = await StartAsync(epoch: 1);
        var router = await CreateRouterAsync(map, first);
        var journals = Enumerable.Range(0, 32).Select(i => JournalId.Create("recover", i.ToString())).ToArray();
        await AppendAsync(router, journals, values: 3);

        // A checkpoint moves the state into checkpoint blobs, and later appends go to the AOF after it.
        await SaveAsync(first);
        await AppendAsync(router, journals, values: 2, start: 3);
        first.Dispose();
        _hosts.Remove(first);

        var (_, second) = await StartAsync(epoch: 2);
        var recovered = await CreateRouterAsync(map, second);
        foreach (var journal in journals)
        {
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, (await JournalWorkload.ReadUnrecordedAsync(recovered, journal)).Values!);
        }
    }

    [Fact]
    public async Task ZombieHost_AcknowledgesNoWriteWhichTheNewHostDoesNotRecover()
    {
        var map = GarnetBlobTestStorage.CreateMap();
        var (zombieStorage, zombie) = await StartAsync(epoch: 1);
        var zombieRouter = await CreateRouterAsync(map, zombie);
        var journals = Enumerable.Range(0, 8).Select(i => JournalId.Create("zombie", i.ToString())).ToArray();

        // Writers append sequence numbers to the zombie while the new host takes the partition over, so AOF commits are
        // in flight during the takeover, and keep appending until the zombie fails them.
        var acknowledged = new int[journals.Length];
        var firstAcknowledged = journals.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var writers = journals.Select((journal, index) => Task.Run(async () =>
        {
            var storage = new GarnetJournalStorage(zombieRouter, journal, "json", compactionThresholdBytes: 0);
            for (var value = 0; ; value++)
            {
                try
                {
                    await storage.AppendAsync(Payload(value), CancellationToken.None);
                    acknowledged[index] = value + 1;
                    firstAcknowledged[index].TrySetResult();
                }
                catch (Exception exception) when (exception is TimeoutException or RedisException)
                {
                    firstAcknowledged[index].TrySetException(exception);
                    return;
                }
            }
        })).ToArray();

        // Take over only once every writer has an acknowledged append, while all of them keep appending.
        await GarnetTestWaits.BoundedAsync(
            Task.WhenAll(firstAcknowledged.Select(signal => signal.Task)),
            "Every writer's first append to the zombie",
            () => Task.FromResult($"Acknowledged appends so far: [{string.Join(", ", acknowledged)}]."),
            TimeSpan.FromSeconds(60));
        var (_, successor) = await StartAsync(epoch: 2);
        var router = await CreateRouterAsync(map, successor);
        await GarnetTestWaits.BoundedAsync(
            Task.WhenAll(writers),
            "Writers to the zombie failing after the takeover",
            () => Task.FromResult($"Acknowledged appends so far: [{string.Join(", ", acknowledged)}]; zombie storage failure: {zombieStorage.Failure?.Message ?? "none"}."),
            TimeSpan.FromSeconds(60));

        // Every append the zombie acknowledged is in the recovered partition, in order and without gaps; an append
        // whose outcome was unknown may be there too.
        for (var i = 0; i < journals.Length; i++)
        {
            var values = (await JournalWorkload.ReadUnrecordedAsync(router, journals[i])).Values!;
            Assert.Equal(Enumerable.Range(0, values.Count), values);
            Assert.InRange(values.Count, acknowledged[i], acknowledged[i] + 1);
        }

        Assert.IsType<GarnetPartitionFencedException>(zombieStorage.Failure);

        // The new host accepts writes, and the zombie acknowledges none.
        await new GarnetJournalStorage(router, journals[0], "json", compactionThresholdBytes: 0).AppendAsync(Payload(-1), CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => new GarnetJournalStorage(zombieRouter, JournalId.Create("zombie", "new"), "json", 0).AppendAsync(Payload(0), CancellationToken).AsTask());
    }

    [Fact]
    public async Task ZombieHost_ServesNoReadsOnceTheNewHostHasTakenOver()
    {
        var map = GarnetBlobTestStorage.CreateMap();
        var (zombieStorage, zombie) = await StartAsync(epoch: 1);
        var zombieRouter = await CreateRouterAsync(map, zombie);
        var journal = JournalId.Create("reads", "0");
        await AppendAsync(zombieRouter, [journal], values: 1);
        Assert.Equal(new[] { 0 }, (await JournalWorkload.ReadUnrecordedAsync(zombieRouter, journal)).Values!);

        // The new host acknowledges an append the zombie never observes.
        var (_, successor) = await StartAsync(epoch: 2);
        var router = await CreateRouterAsync(map, successor);
        await AppendAsync(router, [journal], values: 1, start: 1);

        // The zombie has written nothing since the takeover, so only fenced reads keep it from returning [0].
        var read = await Record.ExceptionAsync(() => JournalWorkload.ReadUnrecordedAsync(zombieRouter, journal));
        Assert.True(read is TimeoutException or RedisException, $"The zombie served a read: {read}");
        Assert.IsType<GarnetPartitionFencedException>(zombieStorage.Failure);
        Assert.Equal(new[] { 0, 1 }, (await JournalWorkload.ReadUnrecordedAsync(router, journal)).Values!);
    }

    [Fact]
    public async Task StaleTenure_CannotTakeOverALaterOne()
    {
        var map = GarnetBlobTestStorage.CreateMap();
        var (current, host) = await StartAsync(epoch: 5);
        await Assert.ThrowsAsync<GarnetPartitionFencedException>(() => GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 4, logger: null, CancellationToken));

        // The stale attempt left the current tenure's lease intact.
        var router = await CreateRouterAsync(map, host);
        await AppendAsync(router, [JournalId.Create("stale", "0")], values: 1);
        Assert.Null(current.Failure);

        // A tenure at the same epoch, such as the same host restarted, takes over.
        var (_, restarted) = await StartAsync(epoch: 5);
        var recovered = await CreateRouterAsync(map, restarted);
        Assert.Equal(new[] { 0 }, (await JournalWorkload.ReadUnrecordedAsync(recovered, JournalId.Create("stale", "0"))).Values!);
    }

    [Fact]
    public async Task Takeover_FencesWritesCreationsAndDeletionsOfTheEarlierTenure()
    {
        var earlier = await GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 1, logger: null, CancellationToken);
        await earlier.EnsureCreatedAsync("data/a.0", 1 << 20);
        await earlier.GetPageBlob("data/a.0").UploadPagesAsync(new MemoryStream(new byte[512]), 0, new() { Conditions = earlier.WriteConditions }, CancellationToken);

        // The earlier tenure has created and acquired a blob but not yet registered it, as between those steps of a creation.
        var unregistered = earlier.GetPageBlob("data/b.0");
        await unregistered.CreateAsync(1 << 20, cancellationToken: CancellationToken);
        await unregistered.GetBlobLeaseClient(earlier.LeaseId).AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration, cancellationToken: CancellationToken);

        var later = await GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 2, logger: null, CancellationToken);
        Assert.Equal(new[] { "data/a.0" }, later.List("").Select(entry => entry.Name));

        // Every write, creation, and deletion of the earlier tenure fails at the storage service.
        foreach (var name in new[] { "data/a.0", "data/b.0" })
        {
            var write = await Assert.ThrowsAsync<RequestFailedException>(() => earlier.GetPageBlob(name).UploadPagesAsync(new MemoryStream(new byte[512]), 0, new() { Conditions = earlier.WriteConditions }, CancellationToken));
            Assert.True(GarnetPartitionBlobStorage.IsLeaseFailure(write), write.ToString());
        }

        await Assert.ThrowsAsync<GarnetPartitionFencedException>(() => earlier.EnsureCreatedAsync("data/c.0", 1 << 20));
        Assert.IsType<GarnetPartitionFencedException>(earlier.Failure);
        await Assert.ThrowsAnyAsync<IOException>(() => earlier.DeleteAsync(["data/a.0"], CancellationToken));
        Assert.DoesNotContain("data/c.0", later.List("").Select(entry => entry.Name));

        // The later tenure writes, creates, including over the earlier tenure's unregistered blob, and deletes.
        await later.GetPageBlob("data/a.0").UploadPagesAsync(new MemoryStream(new byte[512]), 0, new() { Conditions = later.WriteConditions }, CancellationToken);
        await later.EnsureCreatedAsync("data/b.0", 1 << 20);
        await later.GetPageBlob("data/b.0").UploadPagesAsync(new MemoryStream(new byte[512]), 0, new() { Conditions = later.WriteConditions }, CancellationToken);
        await later.DeleteAsync(["data/a.0"], CancellationToken);
        Assert.Equal(new[] { "data/b.0" }, later.List("").Select(entry => entry.Name));
        Assert.Null(later.Failure);
    }
    [Fact]
    public async Task DeviceFactory_DeletesDirectoriesWhichItLists()
    {
        var storage = await GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 1, logger: null, CancellationToken);
        await storage.EnsureCreatedAsync("checkpoint/token/info.dat.0", 1 << 20);
        await storage.EnsureCreatedAsync("checkpoint/token/snapshot.dat.0", 1 << 20);
        await storage.EnsureCreatedAsync("checkpoint/kept.0", 1 << 20);
        var factory = new GarnetPartitionBlobDeviceFactory(storage, "checkpoint");

        // ListContents names a directory by a descriptor with an empty file name, which Delete accepts.
        var directory = Assert.Single(factory.ListContents(""), descriptor => descriptor.directoryName == "token");
        Assert.Equal(string.Empty, directory.fileName);
        factory.Delete(directory);

        Assert.Equal(new[] { "checkpoint/kept.0" }, storage.List("").Select(entry => entry.Name));
        Assert.False(await storage.GetPageBlob("checkpoint/token/info.dat.0").ExistsAsync(CancellationToken));
        Assert.Null(storage.Failure);
    }

    [Fact]
    public async Task Device_ReportsAFailedReadOfAMissingRegisteredBlob()
    {
        var storage = await GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 1, logger: null, CancellationToken);
        await storage.EnsureCreatedAsync("log/aof.0", 1 << 20);
        await storage.GetPageBlob("log/aof.0").UploadPagesAsync(new MemoryStream(Enumerable.Repeat((byte)7, 512).ToArray()), 0, new() { Conditions = storage.WriteConditions }, CancellationToken);
        using var device = new GarnetPartitionBlobDevice(storage, "log/aof");
        Assert.Equal((0u, (byte)7), await ReadFirstByteAsync(device));

        // The blob disappears while the manifest still lists it: its data is lost, and the read must say so.
        await storage.GetPageBlob("log/aof.0").DeleteAsync(conditions: new() { LeaseId = storage.LeaseId }, cancellationToken: CancellationToken);
        var (errorCode, _) = await ReadFirstByteAsync(device);
        Assert.NotEqual(0u, errorCode);
    }

    [Fact]
    public async Task HostRegistry_RegistersListsAndUnregistersHosts_OutsidePartitionStorage()
    {
        var registry = new GarnetJournalHostRegistry(_container);
        Assert.Empty(await registry.ListAsync(CancellationToken));
        await registry.RegisterAsync("127.0.0.1:11111@1", CancellationToken);
        await registry.RegisterAsync("silo/b", CancellationToken);
        await registry.RegisterAsync("silo/b", CancellationToken);

        // A partition's takeover fences only its own location, never the registry.
        var storage = await GarnetPartitionBlobStorage.OpenAsync(_container, "p0", epoch: 1, logger: null, CancellationToken);
        Assert.Empty(storage.List(""));
        Assert.Equal(new[] { "127.0.0.1:11111@1", "silo/b" }, (await registry.ListAsync(CancellationToken)).Order(StringComparer.Ordinal));

        await registry.UnregisterAsync("silo/b", CancellationToken);
        await registry.UnregisterAsync("absent", CancellationToken);
        Assert.Equal(new[] { "127.0.0.1:11111@1" }, await registry.ListAsync(CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _container.DeleteIfExistsAsync();
    }

    private async Task<(GarnetPartitionBlobStorage Storage, GarnetJournalPartitionHost Host)> StartAsync(long epoch)
    {
        var started = await GarnetBlobTestStorage.StartAsync(_container, "p0", epoch, CancellationToken);
        _hosts.Add(started.Host);
        return started;
    }

    private async Task<GarnetJournalRouter> CreateRouterAsync(RangeMap map, GarnetJournalPartitionHost host)
    {
        var router = await GarnetBlobTestStorage.CreateRouterAsync(new InMemoryRangeMetaStore(), map, host.EndPoint, OperationTimeout, CancellationToken);
        _disposables.Add(router);
        return router;
    }

    private static async Task AppendAsync(GarnetJournalRouter router, IEnumerable<JournalId> journals, int values, int start = 0)
    {
        await Parallel.ForEachAsync(journals, CancellationToken, async (journal, token) =>
        {
            var storage = new GarnetJournalStorage(router, journal, "json", compactionThresholdBytes: 0);
            for (var value = start; value < start + values; value++)
            {
                await storage.AppendAsync(Payload(value), token);
            }
        });
    }

    private static ReadOnlySequence<byte> Payload(int value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return new(payload);
    }

    /// <summary>Reads the first sector of segment 0 through a device, returning the callback's error code and the first byte.</summary>
    private static async Task<(uint ErrorCode, byte First)> ReadFirstByteAsync(GarnetPartitionBlobDevice device)
    {
        const int Length = 512;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(Length);
        try
        {
            var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.ReadAsync(0, 0, buffer, Length, (errorCode, _, _, _) => completion.TrySetResult(errorCode), context: completion);
            var errorCode = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);
            return (errorCode, System.Runtime.InteropServices.Marshal.ReadByte(buffer));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task SaveAsync(GarnetJournalPartitionHost host)
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { host.EndPoint }, AllowAdmin = true, Protocol = RedisProtocol.Resp2 });

        // SAVE replies once the checkpoint is complete.
        await admin.GetDatabase().ExecuteAsync("SAVE");
    }
}
