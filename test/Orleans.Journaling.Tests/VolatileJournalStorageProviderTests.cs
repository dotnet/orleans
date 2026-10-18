using System.Buffers;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageProviderTests
{
    [Fact]
    public async Task Compaction_ByteThresholdExcludesSnapshotAndResetsAfterReplacement()
    {
        var storage = new VolatileJournalStorage(null, new()
        {
            MaxAppendsBeforeSnapshot = int.MaxValue,
            MaxBytesBeforeSnapshot = 1000
        });
        var token = TestContext.Current.CancellationToken;
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[1000]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[999]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(2000, storage.Segments.Sum(static segment => segment.Length));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[2000]), token);
        Assert.False(storage.IsCompactionRequested);
        Assert.Equal(2000, Assert.Single(storage.Segments).Length);
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[999]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), token);
        Assert.True(storage.IsCompactionRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compaction_DefaultAppendThresholdAndResetExcludeSnapshot(bool recreate)
    {
        var storage = new VolatileJournalStorage();
        var token = TestContext.Current.CancellationToken;
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[1000]), token);
        Assert.False(storage.IsCompactionRequested);
        for (var index = 0; index < 99; index++)
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
            Assert.False(storage.IsCompactionRequested);
        }

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(101, storage.Segments.Count);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([4]), token);
        await storage.DeleteAsync(token);
        Assert.False(storage.IsCompactionRequested);
        Assert.Empty(storage.Segments);
        if (recreate)
        {
            Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        }

        for (var index = 0; index < 99; index++)
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
            Assert.False(storage.IsCompactionRequested);
        }

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(100, storage.Segments.Count);
        Assert.Equal([2], storage.Segments[^1]);
    }

    [Fact]
    public async Task Compaction_DefaultByteThresholdRequestsAtOneMiBIndependentlyOfAppendCount()
    {
        var storage = new VolatileJournalStorage();
        var token = TestContext.Current.CancellationToken;
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[1024 * 1024 - 1]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(2, storage.Segments.Count);
        Assert.Equal(1024 * 1024, storage.Segments.Sum(static segment => segment.Length));
    }

    [Theory]
    [InlineData(2, 100, false)]
    [InlineData(2, 100, true)]
    [InlineData(100, 2, false)]
    [InlineData(100, 2, true)]
    public async Task Compaction_DeletionResetsBothCountersAndExistingCreationPreservesThem(int appends, long bytes, bool recreate)
    {
        var storage = new VolatileJournalStorage(null, new()
        {
            MaxAppendsBeforeSnapshot = appends,
            MaxBytesBeforeSnapshot = bytes
        });
        var token = TestContext.Current.CancellationToken;
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(2, storage.Segments.Count);

        await storage.DeleteAsync(token);
        Assert.False(storage.IsCompactionRequested);
        Assert.Empty(storage.Segments);
        if (recreate)
        {
            Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        }

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), token);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([4]), token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(2, storage.Segments.Count);
    }

    [Theory]
    [InlineData(3, 100)]
    [InlineData(100, 3)]
    public async Task Compaction_ConfiguredCountersAreSharedAcrossHandlesAndIsolatedAcrossGrains(int appends, long bytes)
    {
        var provider = new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions()),
            Options.Create(new VolatileJournalStorageOptions
            {
                MaxAppendsBeforeSnapshot = appends,
                MaxBytesBeforeSnapshot = bytes
            }), null);
        var firstId = JournalId.FromGrainId(GrainId.Create("test-grain", "first"));
        var first = provider.CreateStorage(firstId);
        var second = provider.CreateStorage(firstId);
        var other = provider.CreateStorage(JournalId.FromGrainId(GrainId.Create("test-grain", "other")));
        var token = TestContext.Current.CancellationToken;
        for (var index = 0; index < 2; index++)
        {
            await first.AppendAsync(new ReadOnlySequence<byte>([1]), token);
            Assert.False(second.IsCompactionRequested);
        }

        await second.AppendAsync(new ReadOnlySequence<byte>([2]), token);
        Assert.True(first.IsCompactionRequested);
        Assert.False(other.IsCompactionRequested);
        Assert.Null(await other.GetMetadataAsync(token));
        await other.AppendAsync(new ReadOnlySequence<byte>([3]), token);
        await second.ReplaceAsync(new ReadOnlySequence<byte>(new byte[100]), token);
        Assert.False(first.IsCompactionRequested);
        Assert.False(other.IsCompactionRequested);
        await first.AppendAsync(new ReadOnlySequence<byte>([4]), token);
        Assert.False(second.IsCompactionRequested);
        await other.AppendAsync(new ReadOnlySequence<byte>([5]), token);
        Assert.False(other.IsCompactionRequested);
        await other.AppendAsync(new ReadOnlySequence<byte>([6]), token);
        Assert.True(other.IsCompactionRequested);
        Assert.False(first.IsCompactionRequested);
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(3, long.MaxValue, false)]
    public async Task Compaction_MultiSegmentAppendCountsOnceAndCopiesBorrowedInput(int length, long maxBytes, bool thresholdReached)
    {
        var storage = new VolatileJournalStorage(null, new()
        {
            MaxAppendsBeforeSnapshot = 2,
            MaxBytesBeforeSnapshot = maxBytes
        });
        byte[] prefix = [1];
        var suffix = Enumerable.Range(2, length - 1).Select(static value => (byte)value).ToArray();
        var first = new BufferSegment(prefix);
        var last = first.Append(suffix);
        var segment = new ReadOnlySequence<byte>(first, 0, last, suffix.Length);
        await storage.AppendAsync(segment, TestContext.Current.CancellationToken);
        Assert.Equal(thresholdReached, storage.IsCompactionRequested);
        prefix[0] = 99;
        suffix[0] = 99;
        Assert.Equal(Enumerable.Range(1, length).Select(static value => (byte)value), Assert.Single(storage.Segments));

        byte[] snapshot = [5, 6, 7, 8];
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(snapshot), TestContext.Current.CancellationToken);
        snapshot[0] = 99;
        Assert.Equal([5, 6, 7, 8], Assert.Single(storage.Segments));
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(3, storage.Segments.Count);
    }

    [Theory]
    [InlineData(8, long.MaxValue)]
    [InlineData(int.MaxValue, 8)]
    public async Task Compaction_ConcurrentHandlesPreserveEveryAppendAndResetTogether(int appends, long bytes)
    {
        var provider = new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions()),
            Options.Create(new VolatileJournalStorageOptions
            {
                MaxAppendsBeforeSnapshot = appends,
                MaxBytesBeforeSnapshot = bytes
            }), null);
        var id = new JournalId("concurrent-thresholds");
        var handles = Enumerable.Range(0, 8).Select(_ => provider.CreateStorage(id)).ToArray();
        var other = provider.CreateStorage(new("isolated-thresholds"));
        var storage = Assert.IsType<VolatileJournalStorage>(handles[0]);
        var token = TestContext.Current.CancellationToken;
        await AppendConcurrently(8);
        Assert.Equal(Enumerable.Range(0, 8).Select(static index => (byte)index),
            storage.Segments.SelectMany(static segment => segment).Order());
        Assert.All(handles, static handle => Assert.True(handle.IsCompactionRequested));
        Assert.False(other.IsCompactionRequested);

        await handles[^1].ReplaceAsync(new ReadOnlySequence<byte>([99]), token);
        Assert.Equal([99], Assert.Single(storage.Segments));
        Assert.All(handles, static handle => Assert.False(handle.IsCompactionRequested));
        await AppendConcurrently(7);
        Assert.Equal(8, storage.Segments.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(static index => (byte)index),
            storage.Segments.Skip(1).SelectMany(static segment => segment).Order());
        Assert.All(handles, static handle => Assert.False(handle.IsCompactionRequested));
        await handles[^1].AppendAsync(new ReadOnlySequence<byte>([7]), token);
        Assert.All(handles, static handle => Assert.True(handle.IsCompactionRequested));
        Assert.False(other.IsCompactionRequested);

        Task AppendConcurrently(int count) => Task.WhenAll(
            Enumerable.Range(0, count).Select(index => Task.Run(
                async () => await handles[index].AppendAsync(new ReadOnlySequence<byte>([(byte)index]), token), token)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compaction_CanceledOperationsPreserveHistoryAndCounters(bool thresholdReached)
    {
        var storage = new VolatileJournalStorage(null, new()
        {
            MaxAppendsBeforeSnapshot = 2,
            MaxBytesBeforeSnapshot = 3
        });
        var token = TestContext.Current.CancellationToken;
        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), token);
        if (thresholdReached)
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>([3]), token);
        }

        var original = storage.Segments.ToArray();
        var metadata = await storage.GetMetadataAsync(token);
        var canceled = new CancellationToken(canceled: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([4]), canceled).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => storage.ReplaceAsync(new ReadOnlySequence<byte>([5]), canceled).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => storage.DeleteAsync(canceled).AsTask());
        Assert.Equal(thresholdReached, storage.IsCompactionRequested);
        Assert.Equal(original, storage.Segments);
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(token))!.ETag);
        await storage.AppendAsync(new ReadOnlySequence<byte>([6]), token);
        Assert.True(storage.IsCompactionRequested);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, long.MinValue)]
    public void Compaction_InvalidLimitsFailAtConstruction(int appends, long bytes)
    {
        var options = new VolatileJournalStorageOptions { MaxAppendsBeforeSnapshot = appends, MaxBytesBeforeSnapshot = bytes };
        var parameter = appends <= 0 ? nameof(options.MaxAppendsBeforeSnapshot) : nameof(options.MaxBytesBeforeSnapshot);
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => new VolatileJournalStorage(null, options));
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions()), Options.Create(options), null));
    }

    [Fact]
    public void Compaction_NullOptionsFailAtConstruction()
    {
        Assert.Throws<ArgumentNullException>("options", () => new VolatileJournalStorage(journalFormatKey: null, options: null!));
        Assert.Throws<ArgumentNullException>("options", () => new VolatileJournalStorageProvider(
            null!, Options.Create(new VolatileJournalStorageOptions()), null));
        Assert.Throws<ArgumentNullException>("storageOptions", () => new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions()), null!, null));
    }

    [Fact]
    public async Task Compaction_ConstructionSnapshotsOptionsAndAcceptsMaximumLimits()
    {
        var options = new VolatileJournalStorageOptions
        {
            MaxAppendsBeforeSnapshot = int.MaxValue,
            MaxBytesBeforeSnapshot = long.MaxValue
        };
        var storage = new VolatileJournalStorage(null, options);
        var provider = new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions()), Options.Create(options), null);
        options.MaxAppendsBeforeSnapshot = 1;
        options.MaxBytesBeforeSnapshot = 1;
        var fromProvider = provider.CreateStorage(new("maximum-limits"));
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await fromProvider.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        Assert.False(fromProvider.IsCompactionRequested);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_MetadataProjectionFlagIsSnapshottedAtEnumerationStart(bool includeMetadata)
    {
        var provider = new VolatileJournalStorageProvider();
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var id in new[] { "a", "b" })
        {
            await provider.CreateStorage(new(id)).CreateIfNotExistsAsync(
                new Dictionary<string, string> { ["owner"] = id }, cancellationToken);
        }

        var options = new JournalCatalogListOptions { IncludeMetadata = !includeMetadata };
        var listing = provider.ListAsync(options, cancellationToken);
        options.IncludeMetadata = includeMetadata;
        await using var enumerator = listing.GetAsyncEnumerator(cancellationToken);
        foreach (var id in new[] { "a", "b" })
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId(id), enumerator.Current.Id);
            if (includeMetadata)
            {
                var metadata = Assert.IsType<JournalMetadata>(enumerator.Current.Metadata);
                var stored = await provider.CreateStorage(new(id)).GetMetadataAsync(cancellationToken);
                Assert.NotNull(stored);
                Assert.Equal(stored.FormatKey, metadata.FormatKey);
                Assert.Equal(stored.ETag, metadata.ETag);
                Assert.Equal(stored.Properties, metadata.Properties);
            }
            else
            {
                Assert.Null(enumerator.Current.Metadata);
            }

            options.IncludeMetadata = !includeMetadata;
        }

        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task ListAsync_MetadataSnapshotPreservesPropertiesAndRejectsStaleConditionalUpdate()
    {
        var provider = new VolatileJournalStorageProvider();
        var id = new JournalId("metadata/snapshot");
        var storage = provider.CreateStorage(id);
        var cancellationToken = TestContext.Current.CancellationToken;
        await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "first" }, cancellationToken);
        await using var enumerator = provider.ListAsync(
            new() { IncludeMetadata = true }, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id, enumerator.Current.Id);
        var snapshot = Assert.IsType<JournalMetadata>(enumerator.Current.Metadata);
        Assert.NotNull(snapshot.ETag);

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "second", ["added"] = "value" },
            expectedETag: snapshot.ETag, cancellationToken: cancellationToken);
        Assert.NotNull(updated);
        Assert.NotEqual(snapshot.ETag, updated.ETag);
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "first" }, snapshot.Properties);
        Assert.Null(await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "stale" },
            expectedETag: snapshot.ETag, cancellationToken: cancellationToken));
        var current = await storage.GetMetadataAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(updated.ETag, current.ETag);
        Assert.Equal(updated.Properties, current.Properties);
    }

    [Fact]
    public async Task ListAsync_UsesRawPrefixAndSnapshotsInclusiveRange()
    {
        var provider = new VolatileJournalStorageProvider();
        string[] ids = ["jobs/20260908-a", "jobs/20260909-a", "jobs/20260909-b", "jobs/20260909-c", "jobs/20260910-a", "other"];
        foreach (var value in ids)
        {
            await provider.CreateStorage(new(value)).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var options = new JournalCatalogListOptions { Prefix = new("other") };
        var listing = provider.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = new("jobs/20260909");
        options.MinId = new("jobs/20260909-b");
        options.MaxId = new("jobs/20260909-c");
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("jobs/20260909-b", enumerator.Current.Id.Value);

        options.Prefix = new("other");
        options.MinId = default;
        options.MaxId = default;
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("jobs/20260909-c", enumerator.Current.Id.Value);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal([new JournalId("other")], await ToListAsync(listing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAsync_RangeIndexIncludesRecreatedStoresAndExcludesDeletedStores()
    {
        var provider = new VolatileJournalStorageProvider();
        var lower = new JournalId("range/b");
        var upper = new JournalId("range/d");
        foreach (var name in new[] { "range/a", "range/b", "range/c", "range/d", "range/e" })
        {
            await provider.CreateStorage(new(name)).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var deleted = provider.CreateStorage(new("range/c"));
        await deleted.DeleteAsync(TestContext.Current.CancellationToken);
        var options = new JournalCatalogListOptions { MinId = lower, MaxId = upper };
        Assert.Equal([lower, upper], await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        await deleted.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([lower, new JournalId("range/c"), upper], await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        options.MinId = new("z");
        Assert.Empty(await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateIfNotExists_ListAndGetMetadataUseJournalIds()
    {
        var provider = new VolatileJournalStorageProvider();
        var idA = JournalId.Create("named", "logs", "a");
        var idB = JournalId.Create("named", "logs", "b");
        var idChild = JournalId.Create("named", "logs", "a", "child");
        var other = JournalId.Create("named", "other", "a");

        var storageA = provider.CreateStorage(idA);
        var created = await storageA.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "one" },
            TestContext.Current.CancellationToken);
        await provider.CreateStorage(idB).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(idChild).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(other).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(created);
        var metadata = await storageA.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.ETag);
        Assert.Equal("one", metadata.Properties["owner"]);

        var alreadyExists = await storageA.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "two" },
            TestContext.Current.CancellationToken);
        Assert.False(alreadyExists);
        Assert.Equal("one", (await storageA.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["owner"]);

        var listed = await ToListAsync(
            provider.ListAsync(new() { Prefix = JournalId.Create("named", "logs") }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal([idA, idChild, idB], listed.OrderBy(id => id.Value, StringComparer.Ordinal));

        Assert.NotNull(await provider.CreateStorage(idB).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Null(await provider.CreateStorage(JournalId.Create("named", "missing")).GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMetadata_UsesETagCasAndReportsNoChange()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "properties", "cas"));
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string>
            {
                ["keep"] = "1",
                ["remove"] = "2"
            },
            TestContext.Current.CancellationToken));
        var original = (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!;

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3", ["add"] = "4" },
            ["remove"],
            original.ETag,
            TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.NotEqual(original.ETag, updated.ETag);
        Assert.Equal("3", updated.Properties["keep"]);
        Assert.Equal("4", updated.Properties["add"]);
        Assert.False(updated.Properties.ContainsKey("remove"));

        var stale = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "5" },
            remove: null,
            original.ETag,
            TestContext.Current.CancellationToken);
        Assert.Null(stale);
        Assert.Equal("3", (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["keep"]);

        var noChange = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3" },
            remove: null,
            updated.ETag,
            TestContext.Current.CancellationToken);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);
    }

    [Fact]
    public async Task StorageOperationsUpdateMetadataAndDeleteRemovesStorage()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalId.Create("named", "conditional", "storage");
        var storage = provider.CreateStorage(storageId);

        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var appendProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(appendProperties);
        Assert.NotNull(appendProperties.ETag);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        var replaceProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(replaceProperties);
        Assert.NotEqual(appendProperties.ETag, replaceProperties.ETag);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken);
        var finalProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(finalProperties);
        Assert.NotEqual(replaceProperties.ETag, finalProperties.ETag);

        Assert.Equal(
            [storageId],
            await ToListAsync(
                provider.ListAsync(new() { Prefix = storageId }, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        await storage.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await ToListAsync(
            provider.ListAsync(new() { Prefix = storageId }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "reserved", "properties"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(
                new Dictionary<string, string> { ["$owner"] = "provider" },
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(
                new Dictionary<string, string> { ["$owner"] = "provider" },
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task<List<JournalId>> ToListAsync(
        IAsyncEnumerable<JournalCatalogEntry> source,
        CancellationToken cancellationToken)
    {
        var result = new List<JournalId>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item.Id);
        }

        return result;
    }
}
