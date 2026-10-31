using System.Buffers;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests bucket migration between partitions: handover, pulls, consolidation, chains, the coordinator rule,
/// reclamation, and tombstone and absent-marker collection.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalMigrationTests : IAsyncLifetime
{
    private const int BucketCount = 4;
    private static readonly PartitionId P0 = GarnetTestPartitions.Id(0);
    private static readonly PartitionId P1 = GarnetTestPartitions.Id(1);
    private static readonly PartitionId P2 = GarnetTestPartitions.Id(2);
    private GarnetTestPartitions _partitions = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // p0 owns buckets 0 and 1, p1 owns 2 and 3, and p2 owns none.
    public async ValueTask InitializeAsync() => _partitions = await GarnetTestPartitions.StartAsync(3, BucketCount, owners: 2, CancellationToken);

    public async ValueTask DisposeAsync() => await _partitions.DisposeAsync();

    [Fact]
    public async Task Handover_MovesJournalsAndReclaimsTheSource()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = _partitions.JournalsInBucket(0, "handover").Take(6).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            await Storage(router, ids[i]).AppendAsync(Bytes((byte)i), CancellationToken);
        }

        await Storage(router, ids[5]).DeleteAsync(CancellationToken);

        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);

        var view = await _partitions.ReadViewAsync();
        Assert.Equal(P2, view.Map.GetOwner(0));
        Assert.Empty(view.Map.Migrations);
        var destination = await router.Migrator.GetBucketAsync(P2, 0, CancellationToken);
        Assert.Equal(("open", true), (destination.State, destination.Done));
        Assert.Equal(5, destination.IndexCount);
        Assert.Equal(1, destination.MarkerCount);
        var source = await router.Migrator.GetBucketAsync(P0, 0, CancellationToken);
        Assert.Equal(("sealed", true, 0L, 0L), (source.State, source.Reclaimed, source.IndexCount, source.MarkerCount));

        var reader = _partitions.CreateRouter();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal([(byte)i], await ReadAsync(reader, ids[i]));
            Assert.False(await _partitions.Database(P0).KeyExistsAsync(Storage(reader, ids[i]).DataKey));
        }

        Assert.Null(await ReadAsync(reader, ids[5]));

        // Reclaimed data reads as an error, never as absent.
        Assert.Null(await router.Migrator.TryReadChainAsync(Storage(reader, ids[0]).Keys, P0, CancellationToken));
    }

    [Fact]
    public async Task Pulls_ServeOperationsBeforeConsolidation()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = _partitions.JournalsInBucket(0, "pull").Take(3).ToArray();
        await Storage(router, ids[0]).AppendAsync(Bytes(1), CancellationToken);
        await Storage(router, ids[1]).AppendAsync(Bytes(1), CancellationToken);

        await coordinator.TryMoveAsync([0], P2, CancellationToken);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);

        // Each first access pulls the journal from the sealed source into the destination.
        var stale = _partitions.CreateRouter();
        var first = Storage(stale, ids[0]);
        Assert.Equal([1], await ReadAsync(stale, ids[0]));
        Assert.True(await _partitions.Database(P2).KeyExistsAsync(first.MetadataKey));
        await first.AppendAsync(Bytes(2), CancellationToken);
        await Storage(stale, ids[1]).DeleteAsync(CancellationToken);
        Assert.Null(await ReadAsync(stale, ids[2]));
        Assert.False((await router.Migrator.GetBucketAsync(P2, 0, CancellationToken)).Done);

        // Consolidation does not overwrite the newer write, and the deleted journal does not resurrect.
        await CompleteAllAsync(coordinator);
        Assert.Equal([1, 2], await ReadAsync(stale, ids[0]));
        Assert.Null(await ReadAsync(stale, ids[1]));
        Assert.Null(await ReadAsync(stale, ids[2]));

        // Once the migration is done, absent markers are dropped, and tombstones once retention expires.
        var options = new GarnetJournalStorageOptions();
        await using var agent = new GarnetJournalMigrationAgent(router, coordinator, partition => partition == P2, options, logger: null);
        Assert.Equal(2, (await router.Migrator.GetBucketAsync(P2, 0, CancellationToken)).MarkerCount);
        Assert.Equal(1, await agent.CollectMarkersAsync(CancellationToken));
        Assert.Equal(1, (await router.Migrator.GetBucketAsync(P2, 0, CancellationToken)).MarkerCount);
        Assert.Equal(1, await router.Migrator.DropMarkersAsync(P2, 0, DateTimeOffset.UtcNow + options.TombstoneRetention, CancellationToken));
        Assert.Equal(0, (await router.Migrator.GetBucketAsync(P2, 0, CancellationToken)).MarkerCount);
        Assert.Null(await ReadAsync(stale, ids[1]));
    }

    [Fact]
    public async Task ChainedMigration_ReadsFollowTheChainAndEveryLinkIsReclaimed()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = _partitions.JournalsInBucket(0, "chain").Take(3).ToArray();
        await Storage(router, ids[0]).AppendAsync(Bytes(1), CancellationToken);
        await Storage(router, ids[1]).AppendAsync(Bytes(1), CancellationToken);

        await coordinator.TryMoveAsync([0], P1, CancellationToken);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);
        await Storage(router, ids[1]).AppendAsync(Bytes(2), CancellationToken);
        await Storage(router, ids[2]).AppendAsync(Bytes(3), CancellationToken);

        // p1 has not consolidated, so the bucket moves on to p2 with a chain p2 -> p1 -> p0.
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
        Assert.Equal(2, (await _partitions.ReadViewAsync()).Map.Migrations.Length);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);
        var reader = _partitions.CreateRouter();
        Assert.Equal([1], await ReadAsync(reader, ids[0]));
        Assert.Equal([1, 2], await ReadAsync(reader, ids[1]));

        await CompleteAllAsync(coordinator);
        Assert.Equal([1], await ReadAsync(reader, ids[0]));
        Assert.Equal([1, 2], await ReadAsync(reader, ids[1]));
        Assert.Equal([3], await ReadAsync(reader, ids[2]));
        foreach (var partition in new[] { P0, P1 })
        {
            var state = await router.Migrator.GetBucketAsync(partition, 0, CancellationToken);
            Assert.True(state.Reclaimed);
            Assert.Equal(0, state.IndexCount + state.MarkerCount);
        }
    }

    [Fact]
    public async Task CoordinatorRule_WaitsForOpenAndForReclamation()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var id = _partitions.JournalsInBucket(0, "rule").First();
        await Storage(router, id).AppendAsync(Bytes(1), CancellationToken);

        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
        Assert.Equal(MoveStatus.PreviousMoveNotOpened, (await coordinator.TryMoveAsync([0], P1, CancellationToken)).Status);

        // p0's sealed copy is on the chain until p2 is done and p0 has reclaimed it.
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);
        Assert.Equal(MoveStatus.DestinationHoldsCopy, (await coordinator.TryMoveAsync([0], P0, CancellationToken)).Status);

        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P0, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.AlreadyOwned, (await coordinator.TryMoveAsync([0], P0, CancellationToken)).Status);

        Assert.Equal([1], await ReadAsync(_partitions.CreateRouter(), id));
        var reopened = await router.Migrator.GetBucketAsync(P0, 0, CancellationToken);
        Assert.Equal(("open", true, false), (reopened.State, reopened.Done, reopened.Reclaimed));
    }

    [Fact]
    public async Task RetryAcrossMigration_ReportsTheCommittedOutcome()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var id = _partitions.JournalsInBucket(1, "retry").First();
        var storage = Storage(router, id);
        await storage.AppendAsync(Bytes(1), CancellationToken);
        var contentETag = (await storage.GetMetadataAsync(CancellationToken))!.ETag!;

        // An append commits in the source, but its reply is lost; the bucket then moves.
        object[] append = [.. storage.GetProcedureKeys(), "1", contentETag, "lost-reply", new byte[] { 2 }, "json", "0"];
        await router.ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.Append, append, CancellationToken);
        await coordinator.TryMoveAsync([1], P2, CancellationToken);
        await CompleteAllAsync(coordinator);

        // The retry, at the new owner, finds its ETag in the pulled journal and reports success without applying again.
        var retried = await _partitions.CreateRouter().ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.Append, append, CancellationToken);
        Assert.Equal(["1", "2"], retried.Select(value => value.ToString()));
        Assert.Equal([1, 2], await ReadAsync(router, id));
    }

    [Fact]
    public async Task Install_IsConditionalOnAbsenceAndTenure()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = _partitions.JournalsInBucket(0, "install").Take(2).ToArray();
        await Storage(router, ids[0]).AppendAsync(Bytes(1), CancellationToken);
        var move = await coordinator.TryMoveAsync([0], P2, CancellationToken);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);
        var version = move.View.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var keys = Storage(router, ids[0]).Keys;
        var record = (await router.Migrator.TryReadChainAsync(keys, P0, CancellationToken))!;
        Assert.Equal(GarnetJournalProtocol.LiveKind, record.Kind);
        Assert.Null(await router.Migrator.TryInstallAsync(keys, P2, "999", record, CancellationToken));
        Assert.True(await router.Migrator.TryInstallAsync(keys, P2, version, record, CancellationToken));
        Assert.False(await router.Migrator.TryInstallAsync(keys, P2, version, record, CancellationToken));

        // The source never held the second journal, so it installs as an absent marker.
        var absent = Storage(router, ids[1]).Keys;
        Assert.Same(ChainRecord.None, await router.Migrator.TryReadChainAsync(absent, P0, CancellationToken));
        Assert.True(await router.Migrator.TryInstallAsync(absent, P2, version, ChainRecord.None, CancellationToken));
        Assert.Equal("1", (string?)await _partitions.Database(P2).HashGetAsync(absent.MetadataKey, "$absent"));
    }

    [Fact]
    public async Task Catalog_ListsJournalsOfBucketsWithIncompleteMigrations()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = _partitions.JournalsInBucket(0, "catalog").Take(5).ToArray();
        foreach (var id in ids)
        {
            await Storage(router, id).CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        }

        await coordinator.TryMoveAsync([0], P2, CancellationToken);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Opened);
        await Storage(router, ids[0]).DeleteAsync(CancellationToken);

        var observer = GarnetPartitionFixture.CreateProvider(dataDirectory: null, _partitions.Store, options => options.HostId = "observer");
        await observer.StartAsync(CancellationToken);
        try
        {
            var listed = new HashSet<JournalId>();
            await foreach (var entry in observer.ListAsync(new() { Prefix = new("catalog") }, CancellationToken))
            {
                listed.Add(entry.Id);
            }

            Assert.True(ids.Skip(1).ToHashSet().SetEquals(listed));
            Assert.True((await router.Migrator.GetBucketAsync(P2, 0, CancellationToken)).Done);
        }
        finally
        {
            await observer.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task SplitAndMerge_MoveSeveralBucketsInOneView()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var ids = Enumerable.Range(0, BucketCount).Select(bucket => _partitions.JournalsInBucket(bucket, "merge").First()).ToArray();
        foreach (var id in ids)
        {
            await Storage(router, id).AppendAsync(Bytes(7), CancellationToken);
        }

        // Merge: buckets of p0 and p1 move to p2 in one view. Split: bucket 3 moves back out to p0.
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([1, 2], P2, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([3], P0, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);

        var map = (await _partitions.ReadViewAsync()).Map;
        Assert.Equal([P0, P2, P2, P0], Enumerable.Range(0, BucketCount).Select(map.GetOwner));
        var reader = _partitions.CreateRouter();
        foreach (var id in ids)
        {
            Assert.Equal([7], await ReadAsync(reader, id));
        }
    }

    [Fact]
    public async Task CoordinatorRule_RequiresTheOwnerToHoldTheBucketOpen()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);

        // An owner which does not hold its bucket open, for example one which has not started yet, could never seal it.
        Assert.True(await router.Migrator.TransitionAsync(P0, 1, GarnetJournalProtocol.BucketSealOperation, 100, source: null, CancellationToken));
        Assert.Equal(MoveStatus.PreviousMoveNotOpened, (await coordinator.TryMoveAsync([1], P2, CancellationToken)).Status);
        Assert.Empty((await _partitions.ReadViewAsync()).Map.Migrations);
    }

    [Fact]
    public async Task UnavailablePartition_DelaysOnlyTheMigrationsWhichNeedIt()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(_partitions.CreateRouter(TimeSpan.FromSeconds(1)));
        var id = _partitions.JournalsInBucket(0, "unavailable").First();
        await Storage(router, id).AppendAsync(Bytes(1), CancellationToken);

        // Bucket 2 moves out of p1, which then stops before it seals; bucket 0 moves between p0 and p2.
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([2], P2, CancellationToken)).Status);
        await _partitions.KillHostAsync(P1);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Complete);
        Assert.Equal(MigrationPhase.Committed, new RangeMapBuckets((await _partitions.ReadViewAsync()).Map).GetLatestMigration(2)!.Value.Phase);

        await _partitions.RestartHostAsync(P1, CancellationToken);
        await CompleteAllAsync(coordinator);
        Assert.Equal([1], await ReadAsync(_partitions.CreateRouter(), id));
    }

    [Fact]
    public async Task DelayedRetry_AfterItsTombstoneIsCollected_DoesNotApplyAgain_WhateverTheClientClocks()
    {
        // The creating silo's clock runs ten seconds ahead of the deleting, hosting silo's. The creating silo retries
        // for 10 minutes, while the hosting silo uses the defaults: 30-second operations and 5-minute tombstone retention.
        var start = DateTimeOffset.UtcNow;
        var writerTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(start + TimeSpan.FromSeconds(10));
        var hostTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(start);
        var writer = _partitions.CreateRouter(TimeSpan.FromMinutes(10), writerTime);
        var host = _partitions.CreateRouter(timeProvider: hostTime);
        var options = new GarnetJournalStorageOptions();
        await using var agent = new GarnetJournalMigrationAgent(host, new GarnetJournalMigrationCoordinator(host), partition => partition == P0 || partition == P1, options, logger: null);
        var id = _partitions.JournalsInBucket(0, "delayed").First();
        var storage = Storage(writer, id);

        // A create, conditioned on the empty absence at the generation it observed, commits, but its reply is lost;
        // another client deletes the journal a second later, by its own clock ten seconds behind.
        var observed = await writer.ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.GetMetadata, storage.GetProcedureKeys(), CancellationToken);
        Assert.Equal(["0", "", "0"], observed.Select(value => value.ToString()));
        object[] create = [.. storage.GetProcedureKeys(), "0", "", "lost-create", new byte[] { 1 }, "json", observed[2].ToString()];
        Assert.Equal("1", (await writer.ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.Append, create, CancellationToken))[0].ToString());
        writerTime.Advance(TimeSpan.FromSeconds(1));
        hostTime.Advance(TimeSpan.FromSeconds(1));
        await Storage(host, id).DeleteAsync(CancellationToken);

        // Six minutes later the host collects the tombstone, advancing the bucket's collection generation past the
        // deletion's, and the retry arrives.
        writerTime.Advance(TimeSpan.FromMinutes(6));
        hostTime.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await agent.CollectMarkersAsync(CancellationToken));
        var collected = await host.Migrator.GetBucketAsync(P0, 0, CancellationToken);
        Assert.Equal((0L, 1L), (collected.MarkerCount, collected.CollectionGeneration));
        Assert.Equal(["-1"], (await writer.ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.Append, create, CancellationToken)).Select(value => value.ToString()));
        Assert.Null(await ReadAsync(host, id));

        // The generation moves with the bucket, so the retry is rejected at its next owner too.
        var coordinator = new GarnetJournalMigrationCoordinator(host);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        Assert.Equal(1, (await host.Migrator.GetBucketAsync(P2, 0, CancellationToken)).CollectionGeneration);
        Assert.Equal(["-1"], (await writer.ExecuteJournalAsync(storage.Keys, GarnetJournalProtocol.Append, create, CancellationToken)).Select(value => value.ToString()));
        Assert.Null(await ReadAsync(host, id));

        // A write which observes the absence afresh applies.
        await Storage(writer, id).AppendAsync(Bytes(2), CancellationToken);
        Assert.Equal([2], await ReadAsync(host, id));
    }

    [Fact]
    public async Task WriteToAbsentJournal_AfterAnUnrelatedCollection_ObservesAgainAndApplies()
    {
        var router = _partitions.CreateRouter();
        var options = new GarnetJournalStorageOptions { TombstoneRetention = TimeSpan.FromMilliseconds(1) };
        await using var agent = new GarnetJournalMigrationAgent(router, new GarnetJournalMigrationCoordinator(router), partition => partition == P0, options, logger: null);
        var ids = _partitions.JournalsInBucket(0, "unrelated").Take(2).ToArray();

        // The writer observes its journal absent; then another journal in the bucket is created, deleted, and collected.
        var writer = Storage(router, ids[0]);
        Assert.Null(await writer.GetMetadataAsync(CancellationToken));
        var other = Storage(router, ids[1]);
        await other.AppendAsync(Bytes(1), CancellationToken);
        await other.DeleteAsync(CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken);
        Assert.Equal(1, await agent.CollectMarkersAsync(CancellationToken));

        // The write's observation predates the collection, so its first attempt is rejected; nothing committed, so the
        // writer observes the empty absence again and writes.
        await writer.AppendAsync(Bytes(2), CancellationToken);
        Assert.Equal([2], await ReadAsync(_partitions.CreateRouter(), ids[0]));
    }

    [Fact]
    public async Task Handover_DoesNotWaitForAnotherBucketsConsolidation()
    {
        var router = _partitions.CreateRouter();
        var consolidating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeSend = async (command, cancellationToken) =>
        {
            if (command == GarnetJournalProtocol.Link)
            {
                consolidating.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var options = new GarnetJournalStorageOptions { MigrationInterval = TimeSpan.FromMilliseconds(20) };
        await using var agent = new GarnetJournalMigrationAgent(router, coordinator, partition => partition == P2, options, logger: null);
        agent.Start();
        try
        {
            // Bucket 0 moves to p2, whose host holds its consolidation.
            Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P2, CancellationToken)).Status);
            await consolidating.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);

            // Bucket 2 moves to p2 too, and its handover completes while bucket 0 is still consolidating.
            var mover = new GarnetJournalMigrationCoordinator(_partitions.CreateRouter());
            Assert.Equal(MoveStatus.Committed, (await mover.TryMoveAsync([2], P2, CancellationToken)).Status);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Phase(await _partitions.ReadViewAsync(), 2) < MigrationPhase.Opened)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Bucket 2's handover did not complete while bucket 0 consolidated: {Phase(await _partitions.ReadViewAsync(), 2)}.");
                await Task.Delay(20, CancellationToken);
            }

            Assert.Equal(MigrationPhase.Opened, Phase(await _partitions.ReadViewAsync(), 0));
        }
        finally
        {
            release.TrySetResult();
        }

        await CompleteAllAsync(coordinator);

        static MigrationPhase? Phase(RangeMapSnapshot view, int bucket) => new RangeMapBuckets(view.Map).GetLatestMigration(bucket)?.Phase;
    }

    [Fact]
    public async Task DelayedDoneAndPurge_ApplyToNoLaterTenure()
    {
        var router = _partitions.CreateRouter();
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var database0 = _partitions.Database(P0);
        var database2 = _partitions.Database(P2);

        // Bucket 1 moves p0 -> p2 -> p0, and a journal is written in p0's second tenure; then it moves to p2 again.
        // A delayed OJDONE for p2's first tenure arrives while p2's second tenure is still consolidating.
        var first = (await coordinator.TryMoveAsync([1], P2, CancellationToken)).View.Version;
        var staleDone = new object[] { GarnetJournalKeySet.GetBucketArgs(_partitions.MapId, 1)[0], first.ToString() };
        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([1], P0, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        var inBucket1 = _partitions.JournalsInBucket(1, "delayed-done").First();
        await Storage(router, inBucket1).AppendAsync(Bytes(7), CancellationToken);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([1], P2, CancellationToken)).Status);
        await AdvanceToAsync(coordinator, 1, MigrationPhase.Opened);
        var done = (RedisResult[])(await database2.ExecuteAsync(GarnetJournalProtocol.Done, staleDone))!;
        Assert.Equal(GarnetJournalProtocol.WrongPartitionStatus.ToString(), done[0].ToString());
        Assert.False((await router.Migrator.GetBucketAsync(P2, 1, CancellationToken)).Done);
        Assert.Equal([7], await ReadAsync(_partitions.CreateRouter(), inBucket1));
        await CompleteAllAsync(coordinator);

        // Bucket 0 moves p0 -> p1, and p0 reclaims its copy; it returns to p0, a journal is written, and it moves on to
        // p2. A delayed purge batch for p0's first tenure arrives after p0 reclaimed its second tenure's copy.
        var id = _partitions.JournalsInBucket(0, "delayed-purge").First();
        var keys = Storage(router, id).Keys;
        var sealedAt = (await coordinator.TryMoveAsync([0], P1, CancellationToken)).View.Version;
        var bucketArgs = GarnetJournalKeySet.GetBucketArgs(_partitions.MapId, 0);
        var stalePurge = new object[] { bucketArgs[0], bucketArgs[1], bucketArgs[2], sealedAt.ToString(), keys.MetadataKey, keys.DataKey, (RedisValue)keys.EncodedId };
        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], P0, CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        await Storage(router, id).AppendAsync(Bytes(9), CancellationToken);
        var resealedAt = (await coordinator.TryMoveAsync([0], P2, CancellationToken)).View.Version;
        await AdvanceToAsync(coordinator, 0, MigrationPhase.Done);
        Assert.True(await router.Migrator.TransitionAsync(P0, 0, GarnetJournalProtocol.BucketReclaimOperation, resealedAt, source: null, CancellationToken));
        var purge = (RedisResult[])(await database0.ExecuteAsync(GarnetJournalProtocol.Purge, stalePurge))!;
        Assert.Equal(GarnetJournalProtocol.WrongPartitionStatus.ToString(), purge[0].ToString());
        Assert.Equal(1, (await router.Migrator.GetBucketAsync(P0, 0, CancellationToken)).IndexCount);

        await CompleteAllAsync(coordinator);
        Assert.Equal([9], await ReadAsync(_partitions.CreateRouter(), id));
    }

    [Fact]
    public async Task Moves_StopAtTheChainLengthWhichReadsTraverse()
    {
        await using var partitions = await GarnetTestPartitions.StartAsync(4, BucketCount, owners: 1, CancellationToken);
        var router = partitions.CreateRouter();
        router.Migrator.MaxChainLength = 2;
        var coordinator = new GarnetJournalMigrationCoordinator(router);
        var id = partitions.JournalsInBucket(0, "chain-limit").First();
        await Storage(router, id).AppendAsync(Bytes(1), CancellationToken);

        // Two moves before either is done chain the bucket through p1 and p2 to p0, at the limit.
        foreach (var destination in new[] { GarnetTestPartitions.Id(1), GarnetTestPartitions.Id(2) })
        {
            Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], destination, CancellationToken)).Status);
            for (var attempt = 0; new RangeMapBuckets((await partitions.ReadViewAsync()).Map).GetLatestMigration(0) is { Phase: < MigrationPhase.Opened }; attempt++)
            {
                Assert.True(attempt < 100, "The move did not open.");
                await coordinator.AdvanceAsync(filter: null, CancellationToken);
            }
        }

        Assert.Equal(MoveStatus.ChainTooLong, (await coordinator.TryMoveAsync([0], GarnetTestPartitions.Id(3), CancellationToken)).Status);

        // A read with the same limit traverses the whole chain.
        var reader = partitions.CreateRouter();
        reader.Migrator.MaxChainLength = 2;
        Assert.Equal([1], await ReadAsync(reader, id));

        // Completing the migrations shortens the chain, and the bucket can move again.
        await CompleteAllAsync(coordinator);
        Assert.Equal(MoveStatus.Committed, (await coordinator.TryMoveAsync([0], GarnetTestPartitions.Id(3), CancellationToken)).Status);
        await CompleteAllAsync(coordinator);
        Assert.Equal([1], await ReadAsync(reader, id));
    }

    private static GarnetJournalStorage Storage(GarnetJournalRouter router, JournalId id) => new(router, id, "json", compactionThresholdBytes: 0);

    private static ReadOnlySequence<byte> Bytes(params byte[] values) => new(values);

    private static async Task<IEnumerable<byte>?> ReadAsync(GarnetJournalRouter router, JournalId id)
    {
        var consumer = new CapturingConsumer();
        await Storage(router, id).ReadAsync(consumer, CancellationToken);
        return consumer.Metadata?.ETag is null ? null : consumer.Bytes.ToArray();
    }

    private static async Task CompleteAllAsync(GarnetJournalMigrationCoordinator coordinator)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await coordinator.CompleteAllAsync(timeout.Token);
    }

    private async Task AdvanceToAsync(GarnetJournalMigrationCoordinator coordinator, int bucket, MigrationPhase phase)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var latest = new RangeMapBuckets((await _partitions.ReadViewAsync()).Map).GetLatestMigration(bucket);
            if (latest is { } step && step.Phase >= phase)
            {
                return;
            }

            await coordinator.AdvanceAsync(filter: null, CancellationToken);
        }

        throw new TimeoutException($"Bucket {bucket} did not reach {phase}.");
    }
}
