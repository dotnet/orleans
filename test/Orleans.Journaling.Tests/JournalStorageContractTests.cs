using System.Buffers;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

internal static class JournalStorageTestExtensions
{
    // Storage instances write only after reading the journal, as the journaled state manager does when it recovers.
    public static async Task<T> ReadForWriteAsync<T>(this T storage) where T : IJournalStorage
    {
        await storage.ReadAsync(new DiscardingConsumer(), TestContext.Current.CancellationToken);
        return storage;
    }

    private sealed class DiscardingConsumer : IJournalStorageConsumer
    {
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata) => buffer.Skip(buffer.Length);
    }
}

internal static class JournalStorageContractTests
{
    public static async Task ObservedAbsenceFencesCompetingCreation(Func<IJournalStorage> create, string operation)
    {
        var token = TestContext.Current.CancellationToken;
        var storage = create();
        await storage.ReadAsync(new Consumer(), token);

        await (await create().ReadForWriteAsync()).AppendAsync(new ReadOnlySequence<byte>([1]), token);

        await Assert.ThrowsAsync<InconsistentStateException>(() => Mutate(storage, operation).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => Mutate(storage, operation).AsTask());
        var consumer = new Consumer();
        await create().ReadAsync(consumer, token);
        Assert.Equal([1], consumer.Bytes);
        Assert.True(consumer.Completed);
    }

    public static async Task MetadataPreservesStalePrecondition(Func<IJournalStorage> create, string operation)
    {
        var token = TestContext.Current.CancellationToken;
        var storage = await create().ReadForWriteAsync();
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), token);
        await (await create().ReadForWriteAsync()).AppendAsync(new ReadOnlySequence<byte>([2]), token);

        Assert.NotNull(await storage.GetMetadataAsync(token));
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        Assert.NotNull(await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "test" }, cancellationToken: token));
        Assert.NotNull(await storage.UpdateMetadataAsync(cancellationToken: token));

        await Assert.ThrowsAsync<InconsistentStateException>(() => Mutate(storage, operation).AsTask());
        var consumer = new Consumer();
        await create().ReadAsync(consumer, token);
        Assert.Equal([1, 2], consumer.Bytes);
        Assert.True(consumer.Completed);
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
