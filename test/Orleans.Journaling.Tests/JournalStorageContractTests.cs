using System.Buffers;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

internal static class JournalStorageContractTests
{
    public static async Task ObservedAbsenceFencesCompetingCreation(Func<IJournalStorage> create, string operation)
    {
        var token = TestContext.Current.CancellationToken;
        var storage = create();
        await storage.ReadAsync(new Consumer(), token);
        var history = Assert.IsAssignableFrom<IJournalHistoryStorage>(storage);
        Assert.Equal(JournalStorageVersion.Absent, history.WriteVersion);

        await create().AppendAsync(new ReadOnlySequence<byte>([1]), token);

        await Assert.ThrowsAsync<InconsistentStateException>(() => Mutate(storage, operation).AsTask());
        Assert.Equal(JournalStorageVersion.Absent, history.WriteVersion);
        var consumer = new Consumer();
        await create().ReadAsync(consumer, token);
        Assert.Equal([1], consumer.Bytes);
        Assert.True(consumer.Completed);
    }

    public static async Task MetadataPreservesStalePrecondition(Func<IJournalStorage> create, string operation)
    {
        var token = TestContext.Current.CancellationToken;
        var storage = create();
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        var history = Assert.IsAssignableFrom<IJournalHistoryStorage>(storage);
        var version = history.WriteVersion;
        await create().AppendAsync(new ReadOnlySequence<byte>([2]), token);

        Assert.NotNull(await storage.GetMetadataAsync(token));
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        Assert.NotNull(await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "test" }, cancellationToken: token));
        Assert.NotNull(await storage.UpdateMetadataAsync(cancellationToken: token));

        Assert.Equal(version, history.WriteVersion);
        await Assert.ThrowsAsync<InconsistentStateException>(() => Mutate(storage, operation).AsTask());
        var consumer = new Consumer();
        await create().ReadAsync(consumer, token);
        Assert.Equal([1, 2], consumer.Bytes);
        Assert.True(consumer.Completed);
    }

    public static async Task HistoryPreservesWriteState(Func<IJournalStorage> create, string change)
    {
        var token = TestContext.Current.CancellationToken;
        var storage = create();
        var history = Assert.IsAssignableFrom<IJournalHistoryStorage>(storage);
        var absent = new Consumer();
        Assert.Equal(JournalStorageVersion.Absent, await history.ReadHistoryAsync(absent, token));
        Assert.Equal(JournalStorageVersion.Unknown, history.WriteVersion);
        Assert.Empty(absent.Bytes);
        Assert.True(absent.Completed);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        var version = history.WriteVersion;
        var compaction = storage.IsCompactionRequested;
        var freshHistory = Assert.IsAssignableFrom<IJournalHistoryStorage>(create());
        var freshConsumer = new Consumer();
        Assert.Equal(version, await freshHistory.ReadHistoryAsync(freshConsumer, token));
        Assert.Equal([1], freshConsumer.Bytes);
        Assert.True(freshConsumer.Completed);
        Assert.Equal(JournalStorageVersion.Unknown, freshHistory.WriteVersion);
        var other = create();
        await other.ReadAsync(new Consumer(), token);
        Assert.Equal(version, Assert.IsAssignableFrom<IJournalHistoryStorage>(other).WriteVersion);
        if (change == "replace")
        {
            await other.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), token);
        }
        else if (change == "delete")
        {
            await other.DeleteAsync(token);
        }

        var expected = new Consumer();
        var reader = create();
        await reader.ReadAsync(expected, token);
        var consumer = new Consumer();
        var observed = await history.ReadHistoryAsync(consumer, token);
        Assert.Equal(Assert.IsAssignableFrom<IJournalHistoryStorage>(reader).WriteVersion, observed);
        Assert.Equal(expected.Bytes, consumer.Bytes);
        Assert.True(consumer.Completed);
        Assert.Equal(version, history.WriteVersion);
        Assert.Equal(compaction, storage.IsCompactionRequested);
        if (change == "none")
        {
            Assert.Equal(version, observed);
            await storage.AppendAsync(new ReadOnlySequence<byte>([4]), token);
            var after = new Consumer();
            await create().ReadAsync(after, token);
            Assert.Equal([1, 4], after.Bytes);
        }
        else
        {
            Assert.NotEqual(version, observed);
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => storage.AppendAsync(new ReadOnlySequence<byte>([4]), token).AsTask());
            var after = new Consumer();
            await create().ReadAsync(after, token);
            Assert.Equal(expected.Bytes, after.Bytes);
        }
    }

    private static ValueTask Mutate(IJournalStorage storage, string operation) => operation switch
    {
        "append" => storage.AppendAsync(new ReadOnlySequence<byte>([9]), TestContext.Current.CancellationToken),
        "replace" => storage.ReplaceAsync(new ReadOnlySequence<byte>([9]), TestContext.Current.CancellationToken),
        "delete" => storage.DeleteAsync(TestContext.Current.CancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private sealed class Consumer : IJournalStorageConsumer
    {
        public List<byte> Bytes { get; } = [];
        public bool Completed { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            Bytes.AddRange(buffer.ToArray());
            buffer.Skip(buffer.Length);
            Completed = buffer.IsCompleted;
        }
    }
}
