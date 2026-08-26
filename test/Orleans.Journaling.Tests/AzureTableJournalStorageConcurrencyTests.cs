using System.Buffers;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureTableJournalStorageConcurrencyTests
{
    private static readonly JournalId TestJournalId = JournalId.FromGrainId(GrainId.Create("table-concurrency", "0"));
    private static readonly string TestPartitionKey = AzureTableJournalStorageOptions.GetDefaultPartitionKey(TestJournalId);

    [Fact]
    public async Task AppendAsync_MultipleMetadataOnlyConflictsWithinRetryLimit_RetriesThenCommitsOnce()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 2);
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "original" },
            TestContext.Current.CancellationToken));
        var originalGeneration = store.HeaderGeneration;
        var metadataWriter = CreateStorage(store);
        store.BeforeTransactionAsync = async (attempt, _, cancellationToken) =>
        {
            if (attempt <= 2)
            {
                Assert.NotNull(await metadataWriter.UpdateMetadataAsync(
                    set: new Dictionary<string, string> { [$"conflict-{attempt}"] = attempt.ToString() },
                    cancellationToken: cancellationToken));
            }
        };

        await storage.AppendAsync(new ReadOnlySequence<byte>([4, 5]), TestContext.Current.CancellationToken);

        Assert.Equal(3, store.TransactionAttempts);
        Assert.Equal(1, store.SuccessfulTransactions);
        Assert.Equal(1, store.DataRowCount);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(1L, store.HeaderRowCount);
        Assert.Equal(2L, store.HeaderLength);
        Assert.Equal(
            [store.TransactionCalls[0][1].RowKey],
            store.DataRowKeys);
        Assert.All(
            store.TransactionCalls,
            transaction => Assert.Equal(store.TransactionCalls[0][1].RowKey, transaction[1].RowKey));

        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["owner"] = "original",
                ["conflict-1"] = "1",
                ["conflict-2"] = "2",
            },
            metadata.Properties);
        Assert.Equal([4, 5], await ReadBytesAsync(store, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_WhenMetadataOnlyConflictsExhaustRetries_FailsAfterExactAttemptsWithoutRows()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 2);
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "original" },
            TestContext.Current.CancellationToken));
        var originalGeneration = store.HeaderGeneration;
        var metadataWriter = CreateStorage(store);
        store.BeforeTransactionAsync = async (attempt, _, cancellationToken) =>
        {
            Assert.NotNull(await metadataWriter.UpdateMetadataAsync(
                set: new Dictionary<string, string> { [$"conflict-{attempt}"] = attempt.ToString() },
                cancellationToken: cancellationToken));
        };

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([7, 8, 9]), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(412, Assert.IsType<RequestFailedException>(exception.InnerException).Status);
        Assert.Equal(3, store.TransactionAttempts);
        Assert.Equal(0, store.SuccessfulTransactions);
        Assert.Equal(0, store.DataRowCount);
        Assert.Empty(store.DataRowKeys);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(0L, store.HeaderRowCount);
        Assert.Equal(0L, store.HeaderLength);
        Assert.All(
            store.TransactionCalls,
            transaction =>
            {
                Assert.Equal(2, transaction.Count);
                Assert.Equal(store.TransactionCalls[0][1].RowKey, transaction[1].RowKey);
            });

        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, metadata.Properties.Count);
        Assert.Equal("3", metadata.Properties["conflict-3"]);
        Assert.Empty(await ReadBytesAsync(store, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_WithZeroRetriesAndZeroBackoff_DoesNotRefreshOrRetry()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 0);
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        var originalGeneration = store.HeaderGeneration;
        store.BeforeTransactionAsync = (attempt, _, _) =>
        {
            Assert.Equal(1, attempt);
            store.MergeHeaderMetadata("conflict", "injected");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, store.TransactionAttempts);
        Assert.Equal(0, store.SuccessfulTransactions);
        Assert.Equal(0, store.HeaderReads);
        Assert.Equal(0, store.DataRowCount);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(0L, store.HeaderRowCount);
        Assert.Equal(0L, store.HeaderLength);
        Assert.Equal("injected", (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["conflict"]);
    }

    [Fact]
    public async Task AppendAsync_WhenCancelledAfterConflict_DoesNotRetryDuringBackoff()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(
            store,
            maxRetries: 3,
            initialBackoff: TimeSpan.FromDays(1),
            maxBackoff: TimeSpan.FromDays(1));
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        var originalGeneration = store.HeaderGeneration;
        var conflictObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeTransactionAsync = (_, _, _) =>
        {
            store.MergeHeaderMetadata("conflict", "before-backoff");
            return Task.CompletedTask;
        };
        store.TransactionConflictObserved = (_, exception) =>
        {
            Assert.Equal(412, exception.Status);
            conflictObserved.TrySetResult();
        };
        using var cancellation = new CancellationTokenSource();

        var appendTask = storage.AppendAsync(new ReadOnlySequence<byte>([2]), cancellation.Token).AsTask();
        await conflictObserved.Task;
        await Task.Yield();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => appendTask);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, store.TransactionAttempts);
        Assert.Equal(0, store.SuccessfulTransactions);
        Assert.Equal(0, store.DataRowCount);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(0L, store.HeaderRowCount);
        Assert.Equal(0L, store.HeaderLength);
        Assert.Equal("before-backoff", (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["conflict"]);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_WhenCreatesOverlap_SecondCoordinatedCallerWinsExactlyOnce()
    {
        var store = new CoordinatedTableStore();
        var firstAddEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeAddAsync = async (attempt, _, cancellationToken) =>
        {
            if (attempt == 1)
            {
                firstAddEntered.TrySetResult();
                await releaseFirstAdd.Task.WaitAsync(cancellationToken);
            }
        };
        var first = CreateStorage(store);
        var second = CreateStorage(store);

        var firstTask = first.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "first" },
            TestContext.Current.CancellationToken).AsTask();
        await firstAddEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondResult = await second.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "second" },
            TestContext.Current.CancellationToken);
        releaseFirstAdd.TrySetResult();
        var firstResult = await firstTask;

        Assert.False(firstResult);
        Assert.True(secondResult);
        Assert.Equal(2, store.AddAttempts);
        Assert.Equal(1, store.SuccessfulAdds);
        Assert.Equal(1, store.EntityCount);
        Assert.Equal(0, store.DataRowCount);
        Assert.False(string.IsNullOrWhiteSpace(store.HeaderGeneration));
        Assert.Equal(0L, store.HeaderRowCount);
        Assert.Equal(0L, store.HeaderLength);
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await CreateStorage(store).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "second" }, metadata.Properties);
    }

    [Fact]
    public async Task AppendAsync_FromSameRecoveredVersion_ContentWinnerCommitsAndLoserRequiresRecovery()
    {
        var store = new CoordinatedTableStore();
        var seed = CreateStorage(store);
        await seed.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var originalGeneration = store.HeaderGeneration;
        var blockedWriter = CreateStorage(store, maxRetries: 3);
        var winningWriter = CreateStorage(store, maxRetries: 3);
        await RecoverAsync(blockedWriter, TestContext.Current.CancellationToken);
        await RecoverAsync(winningWriter, TestContext.Current.CancellationToken);
        var baselineAttempts = store.TransactionAttempts;
        var blockedAttemptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeTransactionAsync = async (attempt, _, cancellationToken) =>
        {
            if (attempt == baselineAttempts + 1)
            {
                blockedAttemptEntered.TrySetResult();
                await releaseBlockedAttempt.Task.WaitAsync(cancellationToken);
            }
        };

        var blockedTask = blockedWriter.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask();
        await blockedAttemptEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await winningWriter.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken);
        releaseBlockedAttempt.TrySetResult();
        var exception = await Assert.ThrowsAsync<InconsistentStateException>(() => blockedTask);

        Assert.Equal(412, Assert.IsType<RequestFailedException>(exception.InnerException).Status);
        Assert.Equal(2, store.TransactionAttempts - baselineAttempts);
        Assert.Equal(2, store.SuccessfulTransactions);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(2L, store.HeaderRowCount);
        Assert.Equal(2L, store.HeaderLength);
        Assert.Equal(2, store.DataRowCount);
        Assert.Equal(2, store.DataRowKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([1, 3], await ReadBytesAsync(store, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMetadataAsync_DisjointConcurrentUpdates_RetryAndMergeBothProperties()
    {
        var store = new CoordinatedTableStore();
        Assert.True(await CreateStorage(store).CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "seed" },
            TestContext.Current.CancellationToken));
        var originalGeneration = store.HeaderGeneration;
        var firstUpdateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeUpdateAsync = async (attempt, _, _, _, cancellationToken) =>
        {
            if (attempt == 1)
            {
                firstUpdateEntered.TrySetResult();
                await releaseFirstUpdate.Task.WaitAsync(cancellationToken);
            }
        };
        var first = CreateStorage(store);
        var second = CreateStorage(store);

        var firstTask = first.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["alpha"] = "A" },
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        await firstUpdateEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondResult = await second.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["beta"] = "B" },
            cancellationToken: TestContext.Current.CancellationToken);
        releaseFirstUpdate.TrySetResult();
        var firstResult = await firstTask;

        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal(3, store.UpdateAttempts);
        Assert.Equal(2, store.SuccessfulUpdates);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(0L, store.HeaderRowCount);
        Assert.Equal(0L, store.HeaderLength);
        Assert.Equal(
            new Dictionary<string, string> { ["owner"] = "seed", ["alpha"] = "A", ["beta"] = "B" },
            firstResult.Properties);
        Assert.Equal(
            new Dictionary<string, string> { ["owner"] = "seed", ["beta"] = "B" },
            secondResult.Properties);
        var final = Assert.IsAssignableFrom<IJournalMetadata>(
            await CreateStorage(store).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal(firstResult.Properties, final.Properties);
        Assert.Equal(firstResult.ETag, final.ETag);
        Assert.Equal(1, store.EntityCount);
    }

    [Fact]
    public async Task UpdateMetadataAsync_WhenAllThreeOptimisticRetriesConflict_ReturnsNullWithoutOverwriting()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store);
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "seed" },
            TestContext.Current.CancellationToken));
        store.BeforeUpdateAsync = (attempt, entity, _, _, _) =>
        {
            Assert.Equal(AzureTableJournalStorage.HeaderRowKey, entity.RowKey);
            store.MergeHeaderMetadata($"conflict-{attempt}", attempt.ToString());
            return Task.CompletedTask;
        };

        var result = await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["owner"] = "overwritten" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(3, store.UpdateAttempts);
        Assert.Equal(0, store.SuccessfulUpdates);
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal("seed", metadata.Properties["owner"]);
        Assert.Equal(["1", "2", "3"], Enumerable.Range(1, 3).Select(index => metadata.Properties[$"conflict-{index}"]));
    }

    [Fact]
    public async Task ReplaceAsync_MultipleMetadataOnlyConflictsWithinRetryLimit_RetriesThenPublishes()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 2);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var oldGeneration = store.HeaderGeneration;
        store.BeforeUpdateAsync = (attempt, entity, _, _, _) =>
        {
            Assert.True(entity.Properties.ContainsKey(AzureTableJournalStorage.GenerationPropertyName));
            if (attempt <= 2)
            {
                store.MergeHeaderMetadata($"conflict-{attempt}", attempt.ToString());
            }

            return Task.CompletedTask;
        };

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([8, 9]), TestContext.Current.CancellationToken);

        Assert.Equal(3, store.UpdateAttempts);
        Assert.Equal(1, store.SuccessfulUpdates);
        Assert.NotEqual(oldGeneration, store.HeaderGeneration);
        Assert.Equal(1L, store.HeaderRowCount);
        Assert.Equal(2L, store.HeaderLength);
        Assert.Equal([8, 9], await ReadBytesAsync(store, TestContext.Current.CancellationToken));
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal("1", metadata.Properties["conflict-1"]);
        Assert.Equal("2", metadata.Properties["conflict-2"]);
    }

    [Fact]
    public async Task DeleteAsync_MultipleMetadataOnlyConflictsWithinRetryLimit_RetriesThenDeletes()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 2);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        store.BeforeDeleteAsync = (attempt, _, _, _) =>
        {
            if (attempt <= 2)
            {
                store.MergeHeaderMetadata($"conflict-{attempt}", attempt.ToString());
            }

            return Task.CompletedTask;
        };

        await storage.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, store.DeleteAttempts);
        Assert.Equal(1, store.SuccessfulDeletes);
        Assert.Equal(0, store.EntityCount);
        Assert.Equal(0, store.DataRowCount);
    }

    [Fact]
    public async Task AppendAsync_WhenHeaderIsDeletedDuringCommit_ReportsRecoveryAndDoesNotWriteRows()
    {
        var store = new CoordinatedTableStore();
        var storage = CreateStorage(store, maxRetries: 2);
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        store.BeforeTransactionAsync = (_, _, _) =>
        {
            store.RemoveHeader();
            return Task.CompletedTask;
        };

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(404, Assert.IsType<RequestFailedException>(exception.InnerException).Status);
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, store.TransactionAttempts);
        Assert.Equal(0, store.SuccessfulTransactions);
        Assert.Equal(0, store.DataRowCount);
        Assert.Equal(0, store.EntityCount);
    }

    [Fact]
    public async Task ReplaceAsync_WhenAppendCommitsBeforeManifestFlip_PreservesAppendAndDeletesUnpublishedRows()
    {
        var store = new CoordinatedTableStore();
        var replacement = CreateStorage(store, maxRetries: 3);
        await replacement.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var originalGeneration = store.HeaderGeneration;
        var appender = CreateStorage(store, maxRetries: 3);
        await RecoverAsync(appender, TestContext.Current.CancellationToken);
        var baselineTransactions = store.TransactionAttempts;
        var flipEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeUpdateAsync = async (attempt, entity, _, _, cancellationToken) =>
        {
            if (attempt == 1 && entity.Properties.ContainsKey(AzureTableJournalStorage.GenerationPropertyName))
            {
                flipEntered.TrySetResult();
                await releaseFlip.Task.WaitAsync(cancellationToken);
            }
        };

        var replaceTask = replacement.ReplaceAsync(new ReadOnlySequence<byte>([9]), TestContext.Current.CancellationToken).AsTask();
        await flipEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await appender.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken);
        releaseFlip.TrySetResult();
        var exception = await Assert.ThrowsAsync<InconsistentStateException>(() => replaceTask);

        Assert.Equal(412, Assert.IsType<RequestFailedException>(exception.InnerException).Status);
        Assert.Equal(3, store.TransactionAttempts - baselineTransactions);
        Assert.Equal(4, store.SuccessfulTransactions);
        Assert.Equal(1, store.UpdateAttempts);
        Assert.Equal(0, store.SuccessfulUpdates);
        Assert.Equal(originalGeneration, store.HeaderGeneration);
        Assert.Equal(2L, store.HeaderRowCount);
        Assert.Equal(2L, store.HeaderLength);
        Assert.Equal(2, store.DataRowCount);
        Assert.Equal(2, store.DataRowKeys.Count(key => key.StartsWith(originalGeneration + "-", StringComparison.Ordinal)));
        Assert.DoesNotContain(store.DataRowKeys, key => !key.StartsWith(originalGeneration + "-", StringComparison.Ordinal));
        Assert.Equal([1, 3], await ReadBytesAsync(store, TestContext.Current.CancellationToken));
    }

    private static AzureTableJournalStorage CreateStorage(
        CoordinatedTableStore store,
        int maxRetries = AzureTableJournalStorageOptions.DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null)
        => new(
            new AzureTableJournalStorage.AzureTableJournalStorageShared(
                NullLogger<AzureTableJournalStorage>.Instance,
                Options.Create(new AzureTableJournalStorageOptions
                {
                    MaxMetadataOnlyConflictRetries = maxRetries,
                    MetadataOnlyConflictInitialBackoff = initialBackoff ?? TimeSpan.Zero,
                    MetadataOnlyConflictMaxBackoff = maxBackoff ?? TimeSpan.Zero,
                }),
                new FakeTableClientProvider(store),
                CreateInstruments(),
                journalFormatKey: "binary"),
            TestJournalId);

    private static AzureTableJournalStorageInstruments CreateInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<AzureTableJournalStorageInstruments>();
        return services.BuildServiceProvider().GetRequiredService<AzureTableJournalStorageInstruments>();
    }

    private static async Task RecoverAsync(
        AzureTableJournalStorage storage,
        CancellationToken cancellationToken)
        => await storage.ReadAsync(DiscardingConsumer.Instance, cancellationToken);

    private static async Task<byte[]> ReadBytesAsync(
        CoordinatedTableStore store,
        CancellationToken cancellationToken)
    {
        var consumer = new CapturingConsumer();
        await CreateStorage(store).ReadAsync(consumer, cancellationToken);
        return consumer.Bytes.ToArray();
    }

    private sealed class DiscardingConsumer : IJournalStorageConsumer
    {
        public static DiscardingConsumer Instance { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata) => buffer.Skip(buffer.Length);
    }

    private sealed class CapturingConsumer : IJournalStorageConsumer
    {
        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length > 0)
            {
                var bytes = new byte[buffer.Length];
                buffer.Read(bytes);
                Bytes.Write(bytes);
            }
        }
    }

    private sealed class FakeTableClientProvider(CoordinatedTableStore store) : AzureTableJournalStorage.TableClientProvider
    {
        public override TableClient GetTableClient() => new FakeTableClient(store);
    }

    private sealed record EntitySnapshot(
        string PartitionKey,
        string RowKey,
        IReadOnlyDictionary<string, object> Properties);

    private sealed record TransactionSnapshot(
        TableTransactionActionType ActionType,
        string PartitionKey,
        string RowKey,
        ETag ETag,
        IReadOnlyDictionary<string, object> Properties);

    private sealed class CoordinatedTableStore
    {
        private readonly object _lock = new();
        private readonly SortedDictionary<(string PartitionKey, string RowKey), StoredEntity> _entities = [];
        private int _version;
        private int _addAttempts;
        private int _successfulAdds;
        private int _headerReads;
        private int _updateAttempts;
        private int _successfulUpdates;
        private int _deleteAttempts;
        private int _successfulDeletes;
        private int _transactionAttempts;
        private int _successfulTransactions;

        public Func<int, EntitySnapshot, CancellationToken, Task>? BeforeAddAsync { get; set; }

        public Func<int, EntitySnapshot, ETag, TableUpdateMode, CancellationToken, Task>? BeforeUpdateAsync { get; set; }

        public Func<int, string, ETag, CancellationToken, Task>? BeforeDeleteAsync { get; set; }

        public Func<int, IReadOnlyList<TransactionSnapshot>, CancellationToken, Task>? BeforeTransactionAsync { get; set; }

        public Action<int, RequestFailedException>? TransactionConflictObserved { get; set; }

        public List<IReadOnlyList<TransactionSnapshot>> TransactionCalls { get; } = [];

        public int AddAttempts => Volatile.Read(ref _addAttempts);

        public int SuccessfulAdds => Volatile.Read(ref _successfulAdds);

        public int HeaderReads => Volatile.Read(ref _headerReads);

        public int UpdateAttempts => Volatile.Read(ref _updateAttempts);

        public int SuccessfulUpdates => Volatile.Read(ref _successfulUpdates);

        public int DeleteAttempts => Volatile.Read(ref _deleteAttempts);

        public int SuccessfulDeletes => Volatile.Read(ref _successfulDeletes);

        public int TransactionAttempts => Volatile.Read(ref _transactionAttempts);

        public int SuccessfulTransactions => Volatile.Read(ref _successfulTransactions);

        public int EntityCount
        {
            get
            {
                lock (_lock)
                {
                    return _entities.Count;
                }
            }
        }

        public int DataRowCount => DataRowKeys.Count;

        public IReadOnlyList<string> DataRowKeys
        {
            get
            {
                lock (_lock)
                {
                    return _entities.Keys
                        .Where(key => key.PartitionKey == TestPartitionKey && key.RowKey != AzureTableJournalStorage.HeaderRowKey)
                        .Select(static key => key.RowKey)
                        .ToList();
                }
            }
        }

        public string HeaderGeneration => GetHeaderProperty<string>(AzureTableJournalStorage.GenerationPropertyName);

        public long HeaderRowCount => GetHeaderProperty<long>(AzureTableJournalStorage.RowCountPropertyName);

        public long HeaderLength => GetHeaderProperty<long>(AzureTableJournalStorage.LengthPropertyName);

        public void MergeHeaderMetadata(string key, string value)
        {
            lock (_lock)
            {
                var entity = _entities[(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey)];
                var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    (string)entity.Properties[AzureTableJournalStorage.MetadataPropertyName])!;
                metadata[key] = value;
                var properties = new Dictionary<string, object>(entity.Properties)
                {
                    [AzureTableJournalStorage.MetadataPropertyName] = JsonSerializer.Serialize(metadata),
                };
                _entities[(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey)] =
                    new StoredEntity(properties, NextETag());
            }
        }

        public void RemoveHeader()
        {
            lock (_lock)
            {
                _entities.Remove((TestPartitionKey, AzureTableJournalStorage.HeaderRowKey));
            }
        }

        public async Task<Response> AddEntityAsync(ITableEntity entity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _addAttempts);
            var snapshot = Snapshot(entity);
            if (BeforeAddAsync is { } before)
            {
                await before(attempt, snapshot, cancellationToken);
            }

            lock (_lock)
            {
                var key = (entity.PartitionKey, entity.RowKey);
                if (_entities.ContainsKey(key))
                {
                    throw AlreadyExists();
                }

                var eTag = NextETag();
                _entities[key] = new StoredEntity(new Dictionary<string, object>(snapshot.Properties), eTag);
                Interlocked.Increment(ref _successfulAdds);
                return new FakeResponse(eTag);
            }
        }

        public Task<Response<T>> GetEntityAsync<T>(
            string partitionKey,
            string rowKey,
            CancellationToken cancellationToken)
            where T : ITableEntity
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowKey == AzureTableJournalStorage.HeaderRowKey)
            {
                Interlocked.Increment(ref _headerReads);
            }

            lock (_lock)
            {
                if (!_entities.TryGetValue((partitionKey, rowKey), out var stored))
                {
                    throw NotFound();
                }

                var entity = (T)(object)CreateEntity(partitionKey, rowKey, stored);
                return Task.FromResult(Response.FromValue(entity, new FakeResponse(stored.ETag)));
            }
        }

        public async Task<Response> UpdateEntityAsync(
            ITableEntity entity,
            ETag ifMatch,
            TableUpdateMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _updateAttempts);
            var snapshot = Snapshot(entity);
            if (BeforeUpdateAsync is { } before)
            {
                await before(attempt, snapshot, ifMatch, mode, cancellationToken);
            }

            lock (_lock)
            {
                var key = (entity.PartitionKey, entity.RowKey);
                if (!_entities.TryGetValue(key, out var stored))
                {
                    throw NotFound();
                }

                if (!ETagMatches(ifMatch, stored.ETag))
                {
                    throw PreconditionFailed();
                }

                var properties = mode == TableUpdateMode.Replace
                    ? new Dictionary<string, object>(snapshot.Properties)
                    : Merge(stored.Properties, snapshot.Properties);
                var eTag = NextETag();
                _entities[key] = new StoredEntity(properties, eTag);
                Interlocked.Increment(ref _successfulUpdates);
                return new FakeResponse(eTag);
            }
        }

        public async Task<Response> DeleteEntityAsync(
            string partitionKey,
            string rowKey,
            ETag ifMatch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _deleteAttempts);
            if (BeforeDeleteAsync is { } before)
            {
                await before(attempt, rowKey, ifMatch, cancellationToken);
            }

            lock (_lock)
            {
                if (!_entities.TryGetValue((partitionKey, rowKey), out var stored))
                {
                    return new FakeResponse(default);
                }

                if (!ETagMatches(ifMatch, stored.ETag))
                {
                    throw PreconditionFailed();
                }

                _entities.Remove((partitionKey, rowKey));
                Interlocked.Increment(ref _successfulDeletes);
                return new FakeResponse(default);
            }
        }

        public async Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actions = transactionActions.ToList();
            var snapshots = actions.Select(Snapshot).ToList();
            var attempt = Interlocked.Increment(ref _transactionAttempts);
            lock (_lock)
            {
                TransactionCalls.Add(snapshots);
            }

            if (BeforeTransactionAsync is { } before)
            {
                await before(attempt, snapshots, cancellationToken);
            }

            RequestFailedException? conflict;
            IReadOnlyList<Response>? responses = null;
            lock (_lock)
            {
                conflict = Validate(actions);
                if (conflict is null)
                {
                    var eTag = NextETag();
                    var mutableResponses = new List<Response>(actions.Count);
                    foreach (var action in actions)
                    {
                        var key = (action.Entity.PartitionKey, action.Entity.RowKey);
                        switch (action.ActionType)
                        {
                            case TableTransactionActionType.Add:
                                _entities[key] = new StoredEntity(ExtractProperties(action.Entity), eTag);
                                mutableResponses.Add(new FakeResponse(eTag));
                                break;
                            case TableTransactionActionType.UpdateMerge:
                            case TableTransactionActionType.UpdateReplace:
                                var current = _entities[key];
                                var replacement = action.ActionType == TableTransactionActionType.UpdateReplace
                                    ? ExtractProperties(action.Entity)
                                    : Merge(current.Properties, ExtractProperties(action.Entity));
                                _entities[key] = new StoredEntity(replacement, eTag);
                                mutableResponses.Add(new FakeResponse(eTag));
                                break;
                            case TableTransactionActionType.Delete:
                                _entities.Remove(key);
                                mutableResponses.Add(new FakeResponse(default));
                                break;
                            default:
                                throw new NotSupportedException($"Unsupported transaction action {action.ActionType}.");
                        }
                    }

                    Interlocked.Increment(ref _successfulTransactions);
                    responses = mutableResponses;
                }
            }

            if (conflict is not null)
            {
                TransactionConflictObserved?.Invoke(attempt, conflict);
                throw conflict;
            }

            return Response.FromValue(
                responses!,
                new FakeResponse(default));
        }

        public AsyncPageable<T> Query<T>(string? filter, CancellationToken cancellationToken)
            where T : ITableEntity
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predicate = ParseFilter(filter);
            List<T> entities;
            lock (_lock)
            {
                entities = _entities
                    .Where(pair => predicate(pair.Key.PartitionKey, pair.Key.RowKey))
                    .Select(pair => (T)(object)CreateEntity(pair.Key.PartitionKey, pair.Key.RowKey, pair.Value))
                    .ToList();
            }

            return AsyncPageable<T>.FromPages(
            [
                Page<T>.FromValues(entities, continuationToken: null, new FakeResponse(default)),
            ]);
        }

        private T GetHeaderProperty<T>(string propertyName)
        {
            lock (_lock)
            {
                return (T)_entities[(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey)].Properties[propertyName];
            }
        }

        private ETag NextETag() => new($"coordinated-{++_version}");

        private RequestFailedException? Validate(IReadOnlyList<TableTransactionAction> actions)
        {
            foreach (var action in actions)
            {
                var key = (action.Entity.PartitionKey, action.Entity.RowKey);
                var exists = _entities.TryGetValue(key, out var stored);
                if (action.ActionType == TableTransactionActionType.Add && exists)
                {
                    return AlreadyExists();
                }

                if (action.ActionType is TableTransactionActionType.UpdateMerge
                        or TableTransactionActionType.UpdateReplace
                        or TableTransactionActionType.Delete)
                {
                    if (!exists)
                    {
                        return NotFound();
                    }

                    if (!ETagMatches(action.ETag, stored!.ETag))
                    {
                        return PreconditionFailed();
                    }
                }
            }

            return null;
        }

        private static bool ETagMatches(ETag expected, ETag actual)
            => expected == default || expected == ETag.All || expected == actual;

        private static Dictionary<string, object> Merge(
            IReadOnlyDictionary<string, object> current,
            IReadOnlyDictionary<string, object> patch)
        {
            var result = new Dictionary<string, object>(current);
            foreach (var (key, value) in patch)
            {
                result[key] = value;
            }

            return result;
        }

        private static EntitySnapshot Snapshot(ITableEntity entity)
            => new(entity.PartitionKey, entity.RowKey, ExtractProperties(entity));

        private static TransactionSnapshot Snapshot(TableTransactionAction action)
            => new(
                action.ActionType,
                action.Entity.PartitionKey,
                action.Entity.RowKey,
                action.ETag,
                ExtractProperties(action.Entity));

        private static Dictionary<string, object> ExtractProperties(ITableEntity entity)
        {
            var result = new Dictionary<string, object>();
            foreach (var (name, value) in (TableEntity)entity)
            {
                if (name is not (nameof(ITableEntity.PartitionKey)
                    or nameof(ITableEntity.RowKey)
                    or nameof(ITableEntity.Timestamp)
                    or "odata.etag"))
                {
                    result[name] = value;
                }
            }

            return result;
        }

        private static TableEntity CreateEntity(string partitionKey, string rowKey, StoredEntity stored)
        {
            var entity = new TableEntity(partitionKey, rowKey);
            foreach (var (name, value) in stored.Properties)
            {
                entity[name] = value;
            }

            entity.ETag = stored.ETag;
            return entity;
        }

        private static Func<string, string, bool> ParseFilter(string? filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return static (_, _) => true;
            }

            var comparisons = filter.Split(" and ", StringSplitOptions.None)
                .Select(static comparison =>
                {
                    var parts = comparison.Split(' ', 3);
                    var property = parts[0];
                    var op = parts[1];
                    var value = parts[2][1..^1].Replace("''", "'");
                    return (property, op, value);
                })
                .ToList();
            return (partitionKey, rowKey) => comparisons.All(comparison =>
            {
                var actual = comparison.property switch
                {
                    nameof(ITableEntity.PartitionKey) => partitionKey,
                    nameof(ITableEntity.RowKey) => rowKey,
                    _ => throw new NotSupportedException($"Unsupported filter property {comparison.property}."),
                };
                var result = string.CompareOrdinal(actual, comparison.value);
                return comparison.op switch
                {
                    "eq" => result == 0,
                    "ge" => result >= 0,
                    "gt" => result > 0,
                    "le" => result <= 0,
                    "lt" => result < 0,
                    _ => throw new NotSupportedException($"Unsupported filter operator {comparison.op}."),
                };
            });
        }

        private static RequestFailedException AlreadyExists()
            => new(409, "The specified entity already exists.", "EntityAlreadyExists", null);

        private static RequestFailedException NotFound()
            => new(404, "The specified resource does not exist.", "ResourceNotFound", null);

        private static RequestFailedException PreconditionFailed()
            => new(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);

        private sealed record StoredEntity(Dictionary<string, object> Properties, ETag ETag);
    }

    private sealed class FakeTableClient(CoordinatedTableStore store) : TableClient
    {
        public override string Name => "journal";

        public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
            => store.AddEntityAsync(entity, cancellationToken);

        public override Task<Response<T>> GetEntityAsync<T>(
            string partitionKey,
            string rowKey,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
            => store.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken);

        public override Task<Response> UpdateEntityAsync<T>(
            T entity,
            ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge,
            CancellationToken cancellationToken = default)
            => store.UpdateEntityAsync(entity, ifMatch, mode, cancellationToken);

        public override Task<Response> DeleteEntityAsync(
            string partitionKey,
            string rowKey,
            ETag ifMatch = default,
            CancellationToken cancellationToken = default)
            => store.DeleteEntityAsync(partitionKey, rowKey, ifMatch, cancellationToken);

        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken = default)
            => store.SubmitTransactionAsync(transactionActions, cancellationToken);

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null,
            int? maxPerPage = null,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
            => store.Query<T>(filter, cancellationToken);
    }

    private sealed class FakeResponse(ETag eTag) : Response
    {
        public override int Status => 204;

        public override string ReasonPhrase => "No Content";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
            => eTag == default ? [] : [new HttpHeader("ETag", eTag.ToString("H"))];

        protected override bool TryGetHeader(string name, out string value)
        {
            if (eTag != default && string.Equals(name, "ETag", StringComparison.OrdinalIgnoreCase))
            {
                value = eTag.ToString("H");
                return true;
            }

            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            if (TryGetHeader(name, out var value))
            {
                values = [value];
                return true;
            }

            values = [];
            return false;
        }
    }
}
