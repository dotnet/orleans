using System.Buffers;
using Orleans.Storage;

namespace Orleans.Journaling.Garnet.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalStorageTests(GarnetPartitionFixture fixture) : IClassFixture<GarnetPartitionFixture>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private GarnetJournalStorageProvider Provider => fixture.Provider;

    private static JournalId NewId(string name) => JournalId.Create("garnet", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public async Task AppendAndRead_RoundTripsBytesAndMetadata()
    {
        var id = NewId("append");
        var storage = Provider.CreateStorage(id);
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "test" }, CancellationToken));
        Assert.False(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "other" }, CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), CancellationToken);
        await storage.AppendAsync(CreateSequence([3, 4], [5]), CancellationToken);

        var metadata = await storage.GetMetadataAsync(CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal("test", metadata.Properties["owner"]);
        Assert.Equal(new JournaledStateManagerOptions().JournalFormatKey, metadata.FormatKey);

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.True(consumer.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], consumer.Bytes);
        Assert.Equal(metadata.FormatKey, consumer.Metadata?.FormatKey);
        Assert.Equal(metadata.ETag, consumer.Metadata?.ETag);
    }

    [Fact]
    public async Task Read_MissingJournal_CompletesWithoutMetadata()
    {
        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(NewId("missing")).ReadAsync(consumer, CancellationToken);
        Assert.True(consumer.IsCompleted);
        Assert.Null(consumer.Metadata?.ETag);
        Assert.Null(await Provider.CreateStorage(NewId("missing")).GetMetadataAsync(CancellationToken));
    }

    [Fact]
    public async Task ReplaceListAndDelete_UseJournalIds()
    {
        var root = $"garnet-list-{Guid.NewGuid():N}";
        var idA = JournalId.Create(root, "list", "a");
        var idB = JournalId.Create(root, "list", "b");
        var child = JournalId.Create(root, "list", "a", "child");
        var other = JournalId.Create(root, "other");

        await Provider.CreateStorage(idA).ReplaceAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        await Provider.CreateStorage(idB).CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        await Provider.CreateStorage(child).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken);
        await Provider.CreateStorage(other).CreateIfNotExistsAsync(cancellationToken: CancellationToken);

        var listed = await ListAsync(new JournalCatalogListOptions { Prefix = JournalId.Create(root, "list") });
        Assert.Equal(3, listed.Count);
        Assert.True(new HashSet<JournalId> { idA, child, idB }.SetEquals(listed));

        await Provider.CreateStorage(idA).DeleteAsync(CancellationToken);

        listed = await ListAsync(new JournalCatalogListOptions { Prefix = JournalId.Create(root, "list") });
        Assert.True(new HashSet<JournalId> { child, idB }.SetEquals(listed));

        listed = await ListAsync(new JournalCatalogListOptions { Prefix = new(root), MinId = idB, MaxId = other });
        Assert.True(new HashSet<JournalId> { idB, other }.SetEquals(listed));
    }

    [Fact]
    public async Task RawUtf16Identities_RoundTripThroughStorageAndCatalog()
    {
        var root = $"garnet-utf16-{Guid.NewGuid():N}/";
        var ids = new JournalId[]
        {
            new(root + "\uD800"), new(root + "\uD801"), new(root + "\uDC00"), new(root + "\uDBFF\uDBFF"),
            new(root + "\uFFFD"), new(root + "%uD800"), new(root + "\U0001F600"), new(root + "*?[x]\\"),
        };
        for (var index = 0; index < ids.Length; index++)
        {
            await Provider.CreateStorage(ids[index]).AppendAsync(new ReadOnlySequence<byte>([(byte)index]), CancellationToken);
        }

        var listed = await ListAsync(new() { Prefix = new(root) });
        Assert.Equal(ids.OrderBy(id => id.Value, StringComparer.Ordinal), listed.OrderBy(id => id.Value, StringComparer.Ordinal));

        var bounded = await ListAsync(new() { Prefix = new(root + "\uD800"), MinId = ids[0], MaxId = ids[0] });
        Assert.Equal([ids[0]], bounded);

        for (var index = 0; index < ids.Length; index++)
        {
            var consumer = new CapturingConsumer();
            await Provider.CreateStorage(ids[index]).ReadAsync(consumer, CancellationToken);
            Assert.Equal([(byte)index], consumer.Bytes);
            await Provider.CreateStorage(ids[index]).DeleteAsync(CancellationToken);
        }

        Assert.Empty(await ListAsync(new() { Prefix = new(root) }));
    }

    [Fact]
    public void JournalIdEncoding_IsInjectiveAndRoundTrips()
    {
        string[] values = ["a", "\uD800", "\uDC00", "\uD800\uDC00", "\uD800\uD800", "\uDC00\uD800", "\U0001F600", "\0x", "é"];
        var encodings = new HashSet<string>();
        foreach (var value in values)
        {
            var encoded = GarnetJournalKeys.EncodeJournalId(value);
            Assert.True(encodings.Add(Convert.ToHexString(encoded)));
            Assert.True(GarnetJournalKeys.TryDecodeJournalId(encoded, out var decoded));
            Assert.Equal(value, decoded.Value);
        }

        // A surrogate pair encoded as two three-byte sequences is not canonical.
        Assert.False(GarnetJournalKeys.TryDecodeJournalId([0xED, 0xA0, 0x80, 0xED, 0xB0, 0x80], out _));
        Assert.False(GarnetJournalKeys.TryDecodeJournalId([0xC0, 0x80], out _));
        Assert.False(GarnetJournalKeys.TryDecodeJournalId([0xE2, 0x82], out _));
    }

    [Fact]
    public async Task UpdateMetadata_UsesETagCasAndPreservesNoChangeETag()
    {
        var storage = Provider.CreateStorage(NewId("metadata"));
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["keep"] = "1", ["remove"] = "2" },
            CancellationToken));
        var original = (await storage.GetMetadataAsync(CancellationToken))!;

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3", ["add"] = "4" },
            ["remove"],
            original.ETag,
            CancellationToken);

        Assert.NotNull(updated);
        Assert.NotEqual(original.ETag, updated.ETag);
        Assert.Equal("3", updated.Properties["keep"]);
        Assert.Equal("4", updated.Properties["add"]);
        Assert.False(updated.Properties.ContainsKey("remove"));

        Assert.Null(await storage.UpdateMetadataAsync(new Dictionary<string, string> { ["keep"] = "5" }, remove: null, original.ETag, CancellationToken));

        var noChange = await storage.UpdateMetadataAsync(new Dictionary<string, string> { ["keep"] = "3" }, remove: null, updated.ETag, CancellationToken);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);

        Assert.Null(await Provider.CreateStorage(NewId("metadata-missing")).UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "1" }, cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task StaleAppend_ThrowsInconsistentStateException()
    {
        var id = NewId("stale");
        var first = Provider.CreateStorage(id);
        var second = Provider.CreateStorage(id);

        await first.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        await second.ReadAsync(new CapturingConsumer(), CancellationToken);
        await first.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => second.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => second.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => second.DeleteAsync(CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes);
    }

    [Fact]
    public async Task ColdAppendToExistingJournal_AppendsCurrentVersion()
    {
        var id = NewId("cold-append");
        await Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        await Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken);

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes);
    }

    [Fact]
    public async Task StaleAppendAfterDelete_DoesNotRecreateJournal()
    {
        var id = NewId("stale-after-delete");
        var stale = Provider.CreateStorage(id);
        await stale.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        await Provider.CreateStorage(id).DeleteAsync(CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => stale.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        Assert.Null(await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken));

        // A deleted journal can be created again.
        await Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken);
        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([3], consumer.Bytes);
    }

    [Fact]
    public async Task Delete_MissingJournal_Succeeds()
    {
        await Provider.CreateStorage(NewId("delete-missing")).DeleteAsync(CancellationToken);
    }

    [Fact]
    public async Task Delete_AfterObservingAbsence_DeletesJournalCreatedSince()
    {
        var id = NewId("delete-cached-absence");
        var deleter = Provider.CreateStorage(id);
        Assert.Null(await deleter.GetMetadataAsync(CancellationToken));

        await Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        await deleter.DeleteAsync(CancellationToken);

        Assert.Null(await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken));
    }

    [Fact]
    public async Task Read_ReturnsOneSegmentPerAppend_AndObservesCancellation()
    {
        var id = NewId("segments");
        var storage = Provider.CreateStorage(id);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([0, 1, 2, 3, 4]), CancellationToken);
        await storage.AppendAsync(new ReadOnlySequence<byte>([5, 6, 7, 8, 9]), CancellationToken);

        var consumer = new FixedRecordConsumer(recordSize: 5);
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.True(consumer.IsCompleted);
        Assert.Equal([[0, 1, 2, 3, 4], [5, 6, 7, 8, 9]], consumer.Records);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var cancelling = new CancellingConsumer(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider.CreateStorage(id).ReadAsync(cancelling, cancellation.Token).AsTask());
        Assert.False(cancelling.IsCompleted);
    }

    [Fact]
    public async Task MetadataOnlyUpdate_DoesNotInvalidateContentWriter()
    {
        var id = NewId("metadata-content-etag");
        var writer = Provider.CreateStorage(id);
        var metadataWriter = Provider.CreateStorage(id);
        await writer.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var metadata = await metadataWriter.GetMetadataAsync(CancellationToken);

        Assert.NotNull(await metadataWriter.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "metadata-writer" },
            expectedETag: metadata!.ETag,
            cancellationToken: CancellationToken));
        await writer.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken);

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes);
        Assert.Equal("metadata-writer", consumer.Metadata!.Properties["owner"]);
    }

    [Fact]
    public async Task ConcurrentReadAndReplace_ReturnsAtomicSnapshot()
    {
        var id = NewId("atomic-read");
        var writer = Provider.CreateStorage(id);
        var first = Enumerable.Repeat((byte)0x11, 512).ToArray();
        var second = Enumerable.Repeat((byte)0x22, 512).ToArray();
        await writer.ReplaceAsync(new ReadOnlySequence<byte>(first), CancellationToken);

        var replaceTask = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await writer.ReplaceAsync(new ReadOnlySequence<byte>(i % 2 == 0 ? second : first), CancellationToken);
            }
        }, CancellationToken);
        var readTasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var i = 0; i < 25; i++)
            {
                var consumer = new CapturingConsumer();
                await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
                var bytes = consumer.Bytes.ToArray();
                Assert.True(bytes.SequenceEqual(first) || bytes.SequenceEqual(second));
            }
        });

        await Task.WhenAll(readTasks.Append(replaceTask));
    }

    [Fact]
    public async Task ConcurrentWriters_ExactlyOneWinsEachRound()
    {
        var id = NewId("concurrent-writers");
        await Provider.CreateStorage(id).CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        var writers = Enumerable.Range(0, 4).Select(_ => Provider.CreateStorage(id)).ToArray();
        var expected = new List<byte>();
        for (var round = 0; round < 10; round++)
        {
            foreach (var writer in writers)
            {
                await writer.GetMetadataAsync(CancellationToken);
            }

            var results = await Task.WhenAll(writers.Select((writer, index) => TryAppendAsync(writer, [(byte)(round * 10 + index)])));
            Assert.Equal(1, results.Count(success => success));
            expected.Add((byte)(round * 10 + Array.IndexOf(results, true)));
        }

        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal(expected, consumer.Bytes);
    }

    [Fact]
    public async Task ConcurrentDeleteAndCreate_NeverLeavesMetadataWithoutData()
    {
        for (var i = 0; i < 32; i++)
        {
            var id = NewId($"delete-create-{i}");
            var deleteTask = Provider.CreateStorage(id).DeleteAsync(CancellationToken).AsTask();
            var appendTask = TryAppendAsync(Provider.CreateStorage(id), [1, 2, 3]);
            await Task.WhenAll(deleteTask, appendTask);

            if (await Provider.CreateStorage(id).GetMetadataAsync(CancellationToken) is not null)
            {
                var consumer = new CapturingConsumer();
                await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
                Assert.Equal([1, 2, 3], consumer.Bytes);
            }
        }
    }

    [Fact]
    public async Task Replace_ResetsCompactionAccounting()
    {
        var id = NewId("compaction");
        var storage = new GarnetJournalStorage(Provider.Router, id, "json", compactionThresholdBytes: 3);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([1, 2, 3, 4]), CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([5, 6]), CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([7]), CancellationToken);
        Assert.True(storage.IsCompactionRequested);

        var reader = new GarnetJournalStorage(Provider.Router, id, "json", compactionThresholdBytes: 3);
        await reader.GetMetadataAsync(CancellationToken);
        Assert.True(reader.IsCompactionRequested);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("01")]
    public async Task InvalidAppendLength_BlocksMutations(string invalidAppendLength)
    {
        var id = NewId("invalid-append-length");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var metadata = await storage.GetMetadataAsync(CancellationToken);
        await fixture.Database.HashSetAsync(storage.MetadataKey, GarnetJournalProtocol.AppendLengthMetadataKey, invalidAppendLength);

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteAsync(CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "invalid" }, expectedETag: metadata!.ETag, cancellationToken: CancellationToken).AsTask());

        await fixture.Database.HashSetAsync(storage.MetadataKey, GarnetJournalProtocol.AppendLengthMetadataKey, "1");
        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1], consumer.Bytes);
        Assert.False(consumer.Metadata!.Properties.ContainsKey("owner"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public async Task MissingOrUnsupportedSchemaVersion_BlocksAccessAndMutations(string? schemaVersion)
    {
        var id = NewId("unsupported-schema");
        var storage = (GarnetJournalStorage)Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken);
        var metadata = await storage.GetMetadataAsync(CancellationToken);
        if (schemaVersion is null)
        {
            await fixture.Database.HashDeleteAsync(storage.MetadataKey, GarnetJournalProtocol.SchemaVersionMetadataKey);
        }
        else
        {
            await fixture.Database.HashSetAsync(storage.MetadataKey, GarnetJournalProtocol.SchemaVersionMetadataKey, schemaVersion);
        }

        await Assert.ThrowsAsync<NotSupportedException>(() => Provider.CreateStorage(id).GetMetadataAsync(CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider.CreateStorage(id).ReadAsync(new CapturingConsumer(), CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "unsupported" }, expectedETag: metadata!.ETag, cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => storage.DeleteAsync(CancellationToken).AsTask());

        await fixture.Database.HashSetAsync(storage.MetadataKey, GarnetJournalProtocol.SchemaVersionMetadataKey, GarnetJournalProtocol.CurrentSchemaVersion);
        var consumer = new CapturingConsumer();
        await Provider.CreateStorage(id).ReadAsync(consumer, CancellationToken);
        Assert.Equal([1], consumer.Bytes);
    }

    [Fact]
    public async Task CallerMayUseFormatMetadata_ButNotProviderOwnedProperties()
    {
        var storage = Provider.CreateStorage(NewId("caller-format"));
        await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["format"] = "caller" }, CancellationToken);
        var metadata = await storage.GetMetadataAsync(CancellationToken);
        Assert.Equal(new JournaledStateManagerOptions().JournalFormatKey, metadata!.FormatKey);
        Assert.Equal("caller", metadata.Properties["format"]);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["$owner"] = "provider" }, CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["$format"] = "provider" }, cancellationToken: CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["x"] = "1" }, remove: ["x"], cancellationToken: CancellationToken).AsTask());
    }

    private async Task<List<JournalId>> ListAsync(JournalCatalogListOptions options)
    {
        var result = new List<JournalId>();
        await foreach (var entry in Provider.ListAsync(options, CancellationToken))
        {
            result.Add(entry.Id);
        }

        Assert.Equal(result.Count, result.Distinct().Count());
        return result;
    }

    private static async Task<bool> TryAppendAsync(IJournalStorage storage, byte[] value)
    {
        try
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>(value), CancellationToken);
            return true;
        }
        catch (InconsistentStateException)
        {
            return false;
        }
    }

    internal static ReadOnlySequence<byte> CreateSequence(byte[] first, byte[] second)
    {
        var firstSegment = new ByteSequenceSegment(first);
        var lastSegment = firstSegment.Append(second);
        return new(firstSegment, 0, lastSegment, lastSegment.Memory.Length);
    }

    private sealed class ByteSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ByteSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class FixedRecordConsumer(int recordSize) : IJournalStorageConsumer
    {
        public List<byte[]> Records { get; } = [];

        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length >= recordSize)
            {
                var record = new byte[recordSize];
                buffer.Read(record);
                Records.Add(record);
            }

            if (buffer.IsCompleted)
            {
                Assert.Equal(0, buffer.Length);
                IsCompleted = true;
            }
        }
    }

    private sealed class CancellingConsumer(CancellationTokenSource cancellation) : IJournalStorageConsumer
    {
        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            buffer.Skip(buffer.Length);
            if (buffer.IsCompleted)
            {
                IsCompleted = true;
            }
            else
            {
                cancellation.Cancel();
            }
        }
    }
}

internal sealed class CapturingConsumer : IJournalStorageConsumer
{
    public List<byte> Bytes { get; } = [];

    public IJournalMetadata? Metadata { get; private set; }

    public bool IsCompleted { get; private set; }

    public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
    {
        Metadata ??= metadata;
        if (buffer.IsCompleted)
        {
            IsCompleted = true;
        }

        var segment = new byte[buffer.Length];
        buffer.Read(segment);
        Bytes.AddRange(segment);
    }
}
