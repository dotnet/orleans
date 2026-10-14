using System.Buffers;
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
    public async Task Initialization_OpensEveryBucketAndRecordsBucketCount()
    {
        var count = 0;
        for (var bucket = 0; bucket < GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT; bucket++)
        {
            var state = await _fixture.Database.HashGetAsync(GarnetJournalKeys.GetBucketKey(bucket), "state");
            count += state == "open" ? 1 : 0;
        }

        Assert.Equal(GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT, count);
        Assert.Equal(GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT.ToString(), (string?)await _fixture.Database.HashGetAsync(GarnetJournalKeys.PartitionKey, "bucket-count"));

        var mismatched = GarnetPartitionFixture.CreateProvider(_fixture.DataDirectory, options =>
        {
            options.DataDirectory = null;
            options.ConfigurationOptions = new ConfigurationOptions { EndPoints = { Provider.EmbeddedEndPoint! } };
            options.BucketCount = 16;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.StartAsync(CancellationToken));
        await mismatched.StopAsync(CancellationToken);
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

        object[] create = [.. Keys(storage), "create-etag", "json", "0"];
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Create, create))[0]);
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Create, create))[0]);
        object[] otherCreate = [.. Keys(storage), "other-create-etag", "json", "0"];
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
        Assert.Equal("1", (await ExecuteAsync(GarnetJournalProtocol.Append, [.. Keys(storage), "0", "", previous, new byte[] { 0 }, "json"]))[0]);
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
        var id = NewId("sealed");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var bucketKey = GarnetJournalKeys.GetBucketKey(storage.Bucket);

        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)bucketKey, "seal", "5", "-", "1"]));
        Assert.Equal(["1"], await ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)bucketKey, "seal", "5", "-", "1"]));
        Assert.Equal(["-1", "sealed", "5"], await ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)bucketKey, "seal", "6", "-", "1"]));
        Assert.Equal(["-1", "sealed", "5"], await ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)bucketKey, "open", "6", "-", "1"]));

        Assert.Equal(["-10", "sealed", "5"], await ExecuteAsync(GarnetJournalProtocol.Read, [.. Keys(storage)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReadAsync(new CapturingConsumer(), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.GetMetadataAsync(CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.UpdateMetadataAsync(cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CreateIfNotExistsAsync(cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteAsync(CancellationToken).AsTask());

        // The sealed bucket's data is intact.
        Assert.Equal(1, await _fixture.Database.ListLengthAsync(storage.DataKey));
    }

    [Fact]
    public async Task IncompleteMigration_RequiresPullOnlyForJournalsAbsentFromPartition()
    {
        var present = (GarnetJournalStorage)Provider.CreateStorage(NewId("present"));
        await present.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var bucketKey = GarnetJournalKeys.GetBucketKey(present.Bucket);
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

    private static object[] Keys(GarnetJournalStorage storage) => storage.GetProcedureKeys();

    private async Task<string?[]> ExecuteAsync(string procedure, object[] args)
    {
        var result = await Provider.Client.ExecuteAsync(procedure, args, CancellationToken);
        return result.Select(value => (string?)value).ToArray();
    }
}
