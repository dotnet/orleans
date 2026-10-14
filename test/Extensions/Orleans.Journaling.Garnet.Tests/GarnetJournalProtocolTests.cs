using System.Buffers;
using Orleans.Journaling.Ranges;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the journal procedures' bucket checks, idempotent retries, and recovery.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalProtocolTests : IAsyncLifetime
{
    private readonly GarnetPartitionFixture _fixture = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private GarnetJournalStorageProvider Provider => _fixture.Provider;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static JournalId NewId(string name) => JournalId.Create("protocol", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public async Task Initialization_OpensEveryBucketAndRecordsPartitionDescriptor()
    {
        var count = 0;
        for (var bucket = 0; bucket < GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT; bucket++)
        {
            var state = await _fixture.Database.HashGetAsync(GarnetJournalKeys.GetBucketKey(Provider.Router.MapId, bucket), "state");
            count += state == "open" ? 1 : 0;
        }

        Assert.Equal(GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT, count);
        Assert.Equal(GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT.ToString(), (string?)await _fixture.Database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.BucketCountField));
        Assert.Equal("p0", (string?)await _fixture.Database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.PartitionIdField));

        // The partition's storage holds 4096 buckets, so a map with another bucket count cannot use it.
        await _fixture.StopAsync();
        var mismatched = GarnetPartitionFixture.CreateProvider(_fixture.DataDirectory, new InMemoryRangeMetaStore(), options => options.BucketCount = 16);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.StartAsync(CancellationToken));
        await mismatched.StopAsync(CancellationToken);
        await _fixture.InitializeAsync();
    }

    [Fact]
    public async Task RetriedMutations_ReportSuccessWithoutApplyingTwice()
    {
        var id = NewId("retry");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        var metadata = (await storage.GetMetadataAsync(CancellationToken))!;
        var contentETag = metadata.ETag!;

        // Replay identical append invocations, as a client does when a reply is lost.
        object[] append = [.. Keys(storage), "1", contentETag, "append-etag", new byte[] { 7 }, "json"];
        Assert.Equal(["1", "1"], await ExecuteAsync(GarnetJournalProtocol.Append, append));
        Assert.Equal(["1", "1"], await ExecuteAsync(GarnetJournalProtocol.Append, append));

        object[] update = [.. Keys(storage), "1", "append-etag", "metadata-etag", "0", "1", "owner", "retry"];
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, update))[0]);
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, update))[0]);

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([7], consumer.Bytes);
        Assert.Equal("metadata-etag", consumer.Metadata!.ETag);
        Assert.Equal("retry", consumer.Metadata.Properties["owner"]);

        object[] delete = [.. Keys(storage), "append-etag", "1", "delete-etag"];
        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Delete, delete));
        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Delete, delete));

        // A different delete expecting the journal finds it absent.
        object[] otherDelete = [.. Keys(storage), "append-etag", "1", "other-etag"];
        Assert.Equal(["-1"], await ExecuteAsync(GarnetJournalProtocol.Delete, otherDelete));
        Assert.Null(await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken));

        object[] create = [.. Keys(storage), "create-etag", "json", "delete-etag", "0"];
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Create, create))[0]);
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Create, create))[0]);
        object[] otherCreate = [.. Keys(storage), "other-create-etag", "json", "delete-etag", "0"];
        Assert.Equal("0", (await ExecuteAsync(GarnetJournalProtocol.Create, otherCreate))[0]);
    }

    [Fact]
    public async Task RetriedMutations_AreRecognizedAfterLaterMutations()
    {
        var id = NewId("retry-later");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);

        // A create-by-append whose reply is lost, followed by another writer's append and delete.
        object[] first = [.. Keys(storage), "0", "", "first", new byte[] { 1 }, "json"];
        Assert.Equal(["1", "1"], await ExecuteAsync(GarnetJournalProtocol.Append, first));
        Assert.Equal(["1", "2"], await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "1", "first", "second", new byte[] { 2 }, "json"]));
        Assert.Equal(["1", "2"], await ExecuteAsync(GarnetJournalProtocol.Append, first));

        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Delete, [.. Keys(storage), "second", "1", "deleted"]));

        // The retry does not resurrect the deleted journal.
        Assert.Equal(["1", "0"], await ExecuteAsync(GarnetJournalProtocol.Append, first));
        Assert.Null(await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken));

        // Retries are recognized while fewer than RecentETagCount later mutations have committed.
        var previous = "recreated-0";
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "0", "deleted", previous, new byte[] { 0 }, "json"]))[0]);
        object[] tracked = [.. Keys(storage), "1", previous, "tracked", new byte[] { 9 }, "json"];
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, tracked))[0]);
        previous = "tracked";
        for (var i = 1; i < GarnetJournalProtocol.RecentETagCount; i++)
        {
            var next = $"later-{i}";
            Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "1", previous, next, new byte[] { 0 }, "json"]))[0]);
            previous = next;
        }

        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, tracked))[0]);
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "1", previous, "evicting", new byte[] { 0 }, "json"]))[0]);
        Assert.Equal(["-1"], await ExecuteAsync(GarnetJournalProtocol.Append, tracked));

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal(GarnetJournalProtocol.RecentETagCount + 2, consumer.Bytes.Count);
        Assert.Equal(1, consumer.Bytes.Count(value => value == 9));
    }

    [Theory]
    [InlineData(GarnetJournalProtocol.Append)]
    [InlineData(GarnetJournalProtocol.Replace)]
    [InlineData(GarnetJournalProtocol.Create)]
    public async Task CreationRetries_DoNotResurrectDeletedJournal_AfterHistoryEviction(string procedure)
    {
        var id = NewId($"resurrect-{procedure}");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        object[] creation = procedure == GarnetJournalProtocol.Create
            ? [.. Keys(storage), "created", "json", "", "1", "owner", "first"]
            : [.. Keys(storage), "0", "", "created", new byte[] { 1 }, "json"];

        // The creation commits but its reply is lost; enough later mutations to evict its ETag end in a deletion.
        Assert.Equal("1", (await ExecuteAsync(procedure, creation))[0]);
        var previous = "created";
        for (var i = 1; i < GarnetJournalProtocol.RecentETagCount; i++)
        {
            var next = $"later-{i}";
            Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "1", previous, next, new byte[] { 2 }, "json"]))[0]);
            previous = next;
        }

        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Delete, [.. Keys(storage), previous, "1", "deleted"]));

        // The retry names the absence it observed (no record), not the deletion's, so it does not create the journal again.
        Assert.Equal(["-1"], await ExecuteAsync(procedure, creation));
        Assert.Null(await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken));
        Assert.Equal(["0", "deleted"], await ExecuteAsync(GarnetJournalProtocol.GetMetadata, [.. Keys(storage)]));
    }

    [Fact]
    public async Task MetadataUpdateRetries_DoNotApplyAgain_AfterHistoryEviction()
    {
        var id = NewId("metadata-eviction");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        var initial = (await storage.GetMetadataAsync(CancellationToken))!.ETag!;

        // An update setting owner=A commits but its reply is lost; later updates end with owner=B.
        object[] setA = [.. Keys(storage), "1", initial, "set-a", "0", "1", "owner", "A"];
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, setA))[0]);
        var previous = "set-a";
        for (var i = 0; i < GarnetJournalProtocol.RecentETagCount; i++)
        {
            var next = $"update-{i}";
            Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, [.. Keys(storage), "1", previous, next, "0", "1", "owner", $"B{i}"]))[0]);
            previous = next;
        }

        // The retry's condition names a metadata ETag which never recurs, so it fails instead of overwriting B.
        Assert.Equal(["-1"], await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, setA));
        var metadata = await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken);
        Assert.Equal($"B{GarnetJournalProtocol.RecentETagCount - 1}", metadata!.Properties["owner"]);
    }

    [Fact]
    public async Task NoOpMetadataUpdate_IsRecognizedOnRetry()
    {
        var id = NewId("metadata-noop");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "A" }, CancellationToken);
        var initial = (await storage.GetMetadataAsync(CancellationToken))!.ETag!;

        // An update setting owner=A changes nothing, keeps the metadata ETag, and loses its reply.
        object[] setA = [.. Keys(storage), "1", initial, "noop", "0", "1", "owner", "A"];
        var first = await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, setA);
        Assert.Equal("1", first[0]);
        Assert.Equal(initial, (await storage.GetMetadataAsync(CancellationToken))!.ETag);

        // Another writer sets owner=B; the retry reports the no-op's success without undoing B.
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, [.. Keys(storage), "1", initial, "set-b", "0", "1", "owner", "B"]))[0]);
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, setA))[0]);
        Assert.Equal("B", (await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken))!.Properties["owner"]);
    }

    [Fact]
    public async Task UnconditionalMetadataUpdate_AppliesOverConcurrentUpdates()
    {
        var id = NewId("metadata-unconditional");
        var storage = Provider.CreateStorage(id);
        var other = Provider.CreateStorage(id);
        await storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        await storage.GetMetadataAsync(CancellationToken);
        await other.UpdateMetadataAsync(new Dictionary<string, string> { ["owner"] = "other" }, cancellationToken: CancellationToken);

        // The update is conditioned on the ETag this instance reads, and reads again after the other writer's update.
        var updated = await storage.UpdateMetadataAsync(new Dictionary<string, string> { ["owner"] = "mine" }, cancellationToken: CancellationToken);
        Assert.Equal("mine", updated!.Properties["owner"]);
        Assert.Equal("mine", (await other.GetMetadataAsync(CancellationToken))!.Properties["owner"]);
    }

    [Fact]
    public async Task WritersWhichObservedMissingJournal_CreateItOnce()
    {
        var id = NewId("duplicate-activation");
        var writers = Enumerable.Range(0, 4).Select(_ => Provider.CreateStorage(id)).ToArray();
        foreach (var writer in writers)
        {
            await writer.ReadAsync(new CapturingConsumer(), CancellationToken);
        }

        var results = await Task.WhenAll(writers.Select(async (writer, index) =>
        {
            try
            {
                await writer.AppendAsync(new ReadOnlySequence<byte>([(byte)index]), CancellationToken);
                return true;
            }
            catch (InconsistentStateException)
            {
                return false;
            }
        }));

        Assert.Equal(1, results.Count(success => success));
        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([(byte)Array.IndexOf(results, true)], consumer.Bytes);
    }

    [Fact]
    public async Task Catalog_ListsJournalsWhichExistThroughoutConcurrentDeletes()
    {
        await using var fixture = new GarnetPartitionFixture { Configure = options => options.BucketCount = 1 };
        await fixture.InitializeAsync();
        var provider = fixture.Provider;
        var stable = Enumerable.Range(0, 300).Select(index => JournalId.Create("catalog", "stable", index.ToString("D3"))).ToArray();
        var transient = Enumerable.Range(0, 300).Select(index => JournalId.Create("catalog", "transient", index.ToString("D3"))).ToArray();
        foreach (var id in stable.Concat(transient))
        {
            await provider.CreateStorage(id).CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        }

        var deletes = Task.Run(async () =>
        {
            foreach (var id in transient)
            {
                await provider.CreateStorage(id).DeleteAsync(CancellationToken);
            }
        }, CancellationToken);

        var listed = new HashSet<JournalId>();
        await foreach (var entry in provider.ListAsync(cancellationToken: CancellationToken))
        {
            listed.Add(entry.Id);
        }

        await deletes;
        Assert.Superset(stable.ToHashSet(), listed);
    }

    [Fact]
    public async Task SealedBucket_RejectsEveryOperation()
    {
        await using var fixture = new GarnetPartitionFixture { Configure = options => options.OperationTimeout = TimeSpan.FromMilliseconds(200) };
        await fixture.InitializeAsync();
        var id = NewId("sealed");
        var storage = (GarnetJournalStorage)fixture.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var bucketKey = storage.BucketKey;

        Assert.Equal(["1"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Bucket, [bucketKey, "seal", "5", "-", "1"]));
        Assert.Equal(["1"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Bucket, [bucketKey, "seal", "5", "-", "1"]));
        Assert.Equal(["-1", "sealed", "5"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Bucket, [bucketKey, "seal", "6", "-", "1"]));
        Assert.Equal(["-1", "sealed", "5"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Bucket, [bucketKey, "open", "6", "-", "1"]));

        Assert.Equal(["-10", "sealed", "5"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Read, [.. Keys(storage)]));
        Assert.Equal(["-10", "sealed", "5"], await ExecuteAsync(fixture.Database, GarnetJournalProtocol.Index, [bucketKey, (RedisKey)GarnetJournalKeys.GetIndexKey(storage.Bucket)]));

        // No view assigns the bucket elsewhere, so every operation retries until it times out.
        await Assert.ThrowsAsync<TimeoutException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.ReadAsync(new CapturingConsumer(), CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.GetMetadataAsync(CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.UpdateMetadataAsync(cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => storage.DeleteAsync(CancellationToken).AsTask());

        // The sealed bucket's data is intact.
        Assert.Equal(1, await fixture.Database.ListLengthAsync(storage.DataKey));
    }

    [Fact]
    public async Task IncompleteMigration_RequiresPullOnlyForJournalsAbsentFromPartition()
    {
        var present = (GarnetJournalStorage)Provider.CreateStorage(NewId("present"));
        await present.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var bucketKey = present.BucketKey;
        await _fixture.Database.HashSetAsync(bucketKey, [new("done", "0"), new("source", "7")]);

        // A journal in the same bucket that the partition does not hold may still be in the source.
        JournalId absentId;
        GarnetJournalStorage absent;
        do
        {
            absentId = NewId("absent");
            absent = (GarnetJournalStorage)Provider.CreateStorage(absentId);
        }
        while (absent.Bucket != present.Bucket);

        Assert.Equal(["-11", "7"], await ExecuteAsync(GarnetJournalProtocol.Read, [.. Keys(absent)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => absent.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(present.JournalId).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1], consumer.Bytes);
        await present.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken);

        // A deletion tombstone is authoritative: the journal reads as absent without consulting the source.
        await present.DeleteAsync(CancellationToken);
        Assert.Null(await Provider.CreateStorage(present.JournalId).GetMetadataAsync(CancellationToken));
    }

    [Fact]
    public async Task AcknowledgedWrites_SurviveRestart()
    {
        var ids = Enumerable.Range(0, 16).Select(index => NewId($"restart-{index}")).ToArray();
        foreach (var id in ids)
        {
            var storage = Provider.CreateStorage(id);
            await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["name"] = id.Value }, CancellationToken);
            await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
            await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken);
            await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken);
        }

        await Provider.CreateStorage(ids[0]).DeleteAsync(CancellationToken);
        await _fixture.Database.ExecuteAsync("SAVE");
        await Provider.CreateStorage(ids[1]).AppendAsync(new ReadOnlySequence<byte>([4]), CancellationToken);
        await Provider.CreateStorage(ids[2]).DeleteAsync(CancellationToken);

        await _fixture.RestartAsync();

        Assert.Null(await Provider.CreateStorage(ids[0]).GetMetadataAsync(CancellationToken));
        Assert.Null(await Provider.CreateStorage(ids[2]).GetMetadataAsync(CancellationToken));
        for (var i = 1; i < ids.Length; i++)
        {
            if (i == 2)
            {
                continue;
            }

            var consumer = new CapturingConsumer();
            await Provider.CreateStorage(ids[i]).ReadAsync(consumer, CancellationToken);
            Assert.Equal(i == 1 ? [2, 3, 4] : [2, 3], consumer.Bytes);
            Assert.Equal(ids[i].Value, consumer.Metadata!.Properties["name"]);
        }

        var listed = new HashSet<JournalId>();
        await foreach (var entry in Provider.ListAsync(new() { Prefix = new("protocol") }, CancellationToken))
        {
            listed.Add(entry.Id);
        }

        Assert.True(ids.Where((_, index) => index is not 0 and not 2).ToHashSet().SetEquals(listed));
    }

    [Fact]
    public async Task DefaultHostId_IsStoredInTheDataDirectoryAndSurvivesRestarts()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        var store = new InMemoryRangeMetaStore();
        var id = NewId("default-host");
        try
        {
            var first = GarnetPartitionFixture.CreateProvider(directory, store, options => options.HostId = null);
            await first.StartAsync(CancellationToken);
            var hostId = (await store.ReadAsync(CancellationToken))!.Map.Partitions[new PartitionId("p0")].Host;
            Assert.Equal(hostId, GarnetJournalStorageProvider.GetOrCreateHostId(directory));
            await first.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
            await first.StopAsync(CancellationToken);

            // The restarted provider recognizes itself as the partition's host, serves it, and republishes its endpoint.
            var second = GarnetPartitionFixture.CreateProvider(directory, store, options => options.HostId = null);
            await second.StartAsync(CancellationToken);
            try
            {
                Assert.Single(second.HostedPartitions);
                var consumer = new CapturingConsumer();
                await second.CreateStorage(id).ReadAsync(consumer, CancellationToken);
                Assert.Equal([1], consumer.Bytes);
            }
            finally
            {
                await second.StopAsync(CancellationToken);
            }
        }
        finally
        {
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }

    private static object[] Keys(GarnetJournalStorage storage) => storage.GetProcedureKeys();

    private Task<string?[]> ExecuteAsync(string procedure, object[] args) => ExecuteAsync(_fixture.Database, procedure, args);

    private static async Task<string?[]> ExecuteAsync(IDatabase database, string procedure, object[] args)
    {
        var result = (RedisResult[])(await database.ExecuteAsync(procedure, args))!;
        return result.Select(value => (string?)value).ToArray();
    }
}
