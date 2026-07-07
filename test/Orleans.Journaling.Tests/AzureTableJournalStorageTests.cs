using System.Buffers;
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

[TestCategory("BVT")]
public sealed class AzureTableJournalStorageTests
{
    private static readonly JournalId TestJournalId = JournalId.FromGrainId(GrainId.Create("test-grain", "0"));
    private static readonly string TestPartitionKey = AzureTableJournalStorageOptions.GetDefaultPartitionKey(TestJournalId);

    [Fact]
    public async Task AppendAsync_RoundTripsThroughRead()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store, journalFormatKey: "binary");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store, journalFormatKey: "binary").ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2, 3], consumer.Bytes.ToArray());
        Assert.Equal("binary", consumer.JournalFormatKey);
        Assert.Equal(2, store.DataRowCount(TestPartitionKey));
    }

    [Fact]
    public async Task AppendAsync_ChunksLargePayloadAcrossPropertiesAndRows()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        // One byte beyond a full row forces a second data row within the same transaction.
        var payload = CreatePayload(15 * 64 * 1024 + 1);
        await storage.AppendAsync(new ReadOnlySequence<byte>(payload), CancellationToken.None);

        var transaction = Assert.Single(store.TransactionCalls);
        Assert.Equal(3, transaction.Count);
        Assert.Equal(TableTransactionActionType.UpdateMerge, transaction[0].ActionType);
        Assert.Equal(AzureTableJournalStorage.HeaderRowKey, transaction[0].RowKey);
        Assert.Equal(TableTransactionActionType.Add, transaction[1].ActionType);
        Assert.Equal(TableTransactionActionType.Add, transaction[2].ActionType);
        Assert.Equal(15, transaction[1].Properties.Count);
        Assert.All(transaction[1].Properties.Values, static value => Assert.Equal(64 * 1024, Assert.IsType<byte[]>(value).Length));
        var tailChunk = Assert.IsType<byte[]>(Assert.Single(transaction[2].Properties).Value);
        Assert.Single(tailChunk);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);

        Assert.Equal(payload, consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAsync_WhenBatchExceedsMaxAppendBytes_ThrowsBeforeRoundTrip()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        var oversize = new ReadOnlySequence<byte>(new byte[AzureTableJournalStorage.MaxAppendBytes + 1]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(oversize, CancellationToken.None).AsTask());

        Assert.Contains("2 MiB", exception.Message);
        Assert.Contains("journal batch", exception.Message);
        Assert.Empty(store.TransactionCalls);
        Assert.Empty(store.AddCalls);
    }

    [Fact]
    public async Task AppendAsync_WhenHeaderETagConflicts_RequiresRecovery()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage(store).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsync_WhenHeaderETagChangesOnlyForMetadata_ReloadsHeaderAndAppends()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);
        var catalogStorage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        Assert.NotNull(await catalogStorage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: CancellationToken.None));

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAsync_AfterSameInstanceMetadataUpdate_UsesUpdatedETag()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        Assert.NotNull(await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: CancellationToken.None));
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(new ETag("update-1"), store.TransactionCalls.Last()[0].ETag);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAsync_WhenJournalRecreatedWithSameShape_RequiresRecovery()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        store.PutEntity(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey, new Dictionary<string, object>
        {
            [AzureTableJournalStorage.FormatPropertyName] = string.Empty,
            [AzureTableJournalStorage.GenerationPropertyName] = "recreated",
            [AzureTableJournalStorage.RowCountPropertyName] = 1L,
            [AzureTableJournalStorage.LengthPropertyName] = 1L,
            [AzureTableJournalStorage.MetadataPropertyName] = "{}",
        });

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsync_WithoutPriorRead_LoadsExistingJournal()
    {
        var store = new FakeTableStore();
        var first = CreateStorage(store);
        await first.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var second = CreateStorage(store);
        await second.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Contains(store.GetCalls, static call => call.RowKey == AzureTableJournalStorage.HeaderRowKey);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReadAsync_WhenHeaderDoesNotExist_CompletesEmpty()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Empty(consumer.Bytes.ToArray());
        Assert.Empty(store.QueryCalls);
    }

    [Fact]
    public async Task ReadAsync_StreamsRowsInOrderAcrossPages()
    {
        var store = new FakeTableStore { PageSize = 1 };
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2, 3], consumer.Bytes.ToArray());
        Assert.Single(store.QueryCalls);
    }

    [Fact]
    public async Task ReadAsync_WhenRowsAreMissing_RequiresRecovery()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        store.RemoveRow(TestPartitionKey, store.DataRowKeys(TestPartitionKey).First());

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateStorage(store).ReadAsync(new CapturingJournalStorageConsumer(), CancellationToken.None).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromRowCount()
    {
        var store = new FakeTableStore();
        var writer = CreateStorage(store);
        await writer.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await writer.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var storage = CreateStorage(store, compactionRowCountThreshold: 2);
        Assert.False(storage.IsCompactionRequested);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.True(storage.IsCompactionRequested);
    }

    [Fact]
    public async Task AppendAsync_UpdatesCompactionRequestFromLength()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store, compactionSizeThreshold: 4);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), CancellationToken.None);
        Assert.False(storage.IsCompactionRequested);

        await storage.AppendAsync(new ReadOnlySequence<byte>([4]), CancellationToken.None);
        Assert.True(storage.IsCompactionRequested);
    }

    [Fact]
    public async Task ReplaceAsync_PublishesNewGenerationAndDeletesPreviousRows()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store, journalFormatKey: "json-lines", compactionRowCountThreshold: 2);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        Assert.True(storage.IsCompactionRequested);
        var previousGeneration = Assert.IsType<string>(
            store.GetProperty(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey, AzureTableJournalStorage.GenerationPropertyName));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([3, 4]), CancellationToken.None);

        // The new generation's rows are written without touching the header, then the header flip
        // publishes them under the last observed ETag, and finally the previous rows are deleted.
        var rowWrite = store.TransactionCalls[2];
        Assert.All(rowWrite, static action => Assert.Equal(TableTransactionActionType.Add, action.ActionType));
        var cleanup = store.TransactionCalls[3];
        Assert.All(cleanup, static action => Assert.Equal(TableTransactionActionType.Delete, action.ActionType));
        Assert.Equal(2, cleanup.Count);
        var flip = store.UpdateCalls.Last();
        Assert.Equal(AzureTableJournalStorage.HeaderRowKey, flip.RowKey);
        Assert.Equal(new ETag("txn-2"), flip.IfMatch);
        var newGeneration = Assert.IsType<string>(flip.Properties[AzureTableJournalStorage.GenerationPropertyName]);
        Assert.NotEqual(previousGeneration, newGeneration);
        Assert.Equal("json-lines", flip.Properties[AzureTableJournalStorage.FormatPropertyName]);

        Assert.False(storage.IsCompactionRequested);
        Assert.Equal(1, store.DataRowCount(TestPartitionKey));

        await storage.AppendAsync(new ReadOnlySequence<byte>([5]), CancellationToken.None);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store, journalFormatKey: "json-lines").ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([3, 4, 5], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_WhenOldGenerationCleanupDisabled_KeepsUnreachablePreviousRows()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store, deleteOldGenerations: false);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, store.DataRowCount(TestPartitionKey));
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_WhenJournalChangesBeforeManifestLoad_RequiresRecovery()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage(store).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([9]), CancellationToken.None).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.DataRowCount(TestPartitionKey));
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_WhenHeaderETagConflictsAtFlip_RequiresRecoveryAndOrphanedRowsAreTolerated()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        var generation = Assert.IsType<string>(
            store.GetProperty(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey, AzureTableJournalStorage.GenerationPropertyName));
        store.BeforeUpdate = (_, rowKey) =>
        {
            if (rowKey == AzureTableJournalStorage.HeaderRowKey)
            {
                // Simulate a competing writer appending one byte between the manifest load and the flip.
                store.BeforeUpdate = null;
                store.PutEntity(TestPartitionKey, AzureTableJournalStorage.HeaderRowKey, new Dictionary<string, object>
                {
                    [AzureTableJournalStorage.FormatPropertyName] = string.Empty,
                    [AzureTableJournalStorage.GenerationPropertyName] = generation,
                    [AzureTableJournalStorage.RowCountPropertyName] = 3L,
                    [AzureTableJournalStorage.LengthPropertyName] = 3L,
                    [AzureTableJournalStorage.MetadataPropertyName] = "{}",
                });
                store.PutEntity(TestPartitionKey, $"{generation}-{2L:D12}", new Dictionary<string, object>
                {
                    ["Data00"] = new byte[] { 8 },
                });
            }
        };

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([9]), CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);

        // The failed flip leaves an unreachable orphan row behind; reads and subsequent replaces
        // are unaffected by it because every generation has a unique prefix.
        Assert.Equal(4, store.DataRowCount(TestPartitionKey));
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2, 8], consumer.Bytes.ToArray());

        var freshStorage = CreateStorage(store);
        await freshStorage.ReplaceAsync(new ReadOnlySequence<byte>([7]), CancellationToken.None);
        var replacedConsumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(replacedConsumer, CancellationToken.None);
        Assert.Equal([7], replacedConsumer.Bytes.ToArray());

        // Deleting the journal removes the header, the current generation, and any orphans.
        await freshStorage.DeleteAsync(CancellationToken.None);
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public async Task ReplaceAsync_WhenHeaderETagChangesOnlyForMetadata_ReloadsHeaderAndFlips()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);
        var catalogStorage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        Assert.NotNull(await catalogStorage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: CancellationToken.None));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var metadata = await CreateStorage(store).GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal("closed", metadata.Properties["catalog"]);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_WhenRowWriteFails_DoesNotFlipHeader()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        store.FailNextTransaction = true;
        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 3], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_AllowsNextAppendToRecreateJournal()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.DeleteAsync(CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, store.AddCalls.Count(static call => call.RowKey == AzureTableJournalStorage.HeaderRowKey));
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RemovesHeaderAndAllRows()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        await storage.DeleteAsync(CancellationToken.None);

        Assert.True(store.IsEmpty);
    }

    [Fact]
    public async Task DeleteAsync_WhenHeaderETagChanges_DoesNotDeleteUpdatedJournal()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await CreateStorage(store).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.DeleteAsync(CancellationToken.None).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_WhenHeaderETagChangesOnlyForMetadata_ReloadsHeaderAndDeletes()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);
        var catalogStorage = CreateStorage(store);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        Assert.NotNull(await catalogStorage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: CancellationToken.None));

        await storage.DeleteAsync(CancellationToken.None);

        Assert.True(store.IsEmpty);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_AppliesInitialMetadataOnlyOnCreation()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);

        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "first" }));
        Assert.False(await CreateStorage(store).CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "second" }));

        var metadata = await storage.GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal("first", metadata.Properties["owner"]);
        Assert.NotNull(metadata.ETag);
    }

    [Fact]
    public async Task GetMetadataAsync_WhenJournalDoesNotExist_ReturnsNull()
    {
        var storage = CreateStorage(new FakeTableStore());

        Assert.Null(await storage.GetMetadataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpdateMetadataAsync_SetsAndRemovesCallerProperties()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }));

        var updated = await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["c"] = "3" },
            remove: ["a"],
            cancellationToken: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(new Dictionary<string, string> { ["b"] = "2", ["c"] = "3" }, updated.Properties);
        var metadata = await CreateStorage(store).GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal(updated.Properties, metadata.Properties);
        Assert.Equal(updated.ETag, metadata.ETag);
    }

    [Fact]
    public async Task UpdateMetadataAsync_WhenExpectedETagDoesNotMatch_ReturnsNull()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store);
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["a"] = "1" }));

        var updated = await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["a"] = "2" },
            expectedETag: "\"bogus\"",
            cancellationToken: CancellationToken.None);

        Assert.Null(updated);
        var metadata = await storage.GetMetadataAsync(CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal("1", metadata.Properties["a"]);
    }

    [Fact]
    public async Task MetadataOperations_RejectProviderOwnedProperties()
    {
        var storage = CreateStorage(new FakeTableStore());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["$provider"] = "1" }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(set: new Dictionary<string, string> { ["$provider"] = "1" }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(remove: ["$provider"]).AsTask());
    }

    [Fact]
    public async Task UpdateMetadataAsync_PreservesProviderProperties()
    {
        var store = new FakeTableStore();
        var storage = CreateStorage(store, journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        Assert.NotNull(await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            cancellationToken: CancellationToken.None));

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(store, journalFormatKey: "json-lines").ReadAsync(consumer, CancellationToken.None);
        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([1], consumer.Bytes.ToArray());
    }

    [Fact]
    public void DefaultPartitionKey_EscapesJournalIdValue()
    {
        Assert.Equal("journals%2Ftest", AzureTableJournalStorageOptions.GetDefaultPartitionKey(new JournalId("journals/test")));
    }

    [Fact]
    public void GetPartitionKeyForJournal_UsesConfiguredPartitionKey()
    {
        var options = new AzureTableJournalStorageOptions
        {
            GetPartitionKey = static journalId => $"custom-{journalId.Value}",
        };

        Assert.Equal($"custom-{TestJournalId.Value}", options.GetPartitionKeyForJournal(TestJournalId));
    }

    private static AzureTableJournalStorage CreateStorage(
        FakeTableStore store,
        string? journalFormatKey = null,
        bool deleteOldGenerations = true,
        long compactionRowCountThreshold = AzureTableJournalStorageOptions.DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD,
        long compactionSizeThreshold = AzureTableJournalStorageOptions.DEFAULT_COMPACTION_SIZE_THRESHOLD)
    {
        return new AzureTableJournalStorage(
            new AzureTableJournalStorage.AzureTableJournalStorageShared(
                NullLogger<AzureTableJournalStorage>.Instance,
                Options.Create(new AzureTableJournalStorageOptions
                {
                    DeleteOldGenerations = deleteOldGenerations,
                    CompactionRowCountThreshold = compactionRowCountThreshold,
                    CompactionSizeThreshold = compactionSizeThreshold,
                }),
                new FakeTableClientProvider(store),
                CreateAzureTableJournalStorageInstruments(),
                journalFormatKey),
            TestJournalId);
    }

    private static AzureTableJournalStorageInstruments CreateAzureTableJournalStorageInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<AzureTableJournalStorageInstruments>();
        return services.BuildServiceProvider().GetRequiredService<AzureTableJournalStorageInstruments>();
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private sealed class DiscardingJournalStorageConsumer : IJournalStorageConsumer
    {
        public static DiscardingJournalStorageConsumer Instance { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata) => buffer.Skip(buffer.Length);
    }

    private sealed class CapturingJournalStorageConsumer : IJournalStorageConsumer
    {
        public string? JournalFormatKey { get; private set; }

        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            JournalFormatKey = metadata?.Format;
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }

    private sealed class FakeTableClientProvider(FakeTableStore store) : AzureTableJournalStorage.TableClientProvider
    {
        public override TableClient GetTableClient() => store.GetTableClient();
    }

    private sealed record AddCall(string PartitionKey, string RowKey, IReadOnlyDictionary<string, object> Properties);

    private sealed record UpdateCall(string PartitionKey, string RowKey, ETag IfMatch, TableUpdateMode Mode, IReadOnlyDictionary<string, object> Properties);

    private sealed record DeleteCall(string PartitionKey, string RowKey, ETag IfMatch);

    private sealed record GetCall(string PartitionKey, string RowKey);

    private sealed record QueryCall(string? Filter);

    private sealed record TransactionAction(TableTransactionActionType ActionType, string PartitionKey, string RowKey, ETag ETag, IReadOnlyDictionary<string, object> Properties);

    private sealed class FakeTableStore
    {
        private readonly SortedDictionary<(string PartitionKey, string RowKey), StoredEntity> _entities = new(KeyComparer.Instance);
        private int _addCount;
        private int _updateCount;
        private int _transactionCount;
        private int _putCount;

        public int PageSize { get; set; } = 1000;

        public bool FailNextTransaction { get; set; }

        public Action<string, string>? BeforeUpdate { get; set; }

        public List<AddCall> AddCalls { get; } = [];

        public List<UpdateCall> UpdateCalls { get; } = [];

        public List<DeleteCall> DeleteCalls { get; } = [];

        public List<GetCall> GetCalls { get; } = [];

        public List<QueryCall> QueryCalls { get; } = [];

        public List<IReadOnlyList<TransactionAction>> TransactionCalls { get; } = [];

        public bool IsEmpty => _entities.Count == 0;

        public TableClient GetTableClient() => new FakeTableClient(this);

        public long DataRowCount(string partitionKey)
            => DataRowKeys(partitionKey).Count();

        public IEnumerable<string> DataRowKeys(string partitionKey)
            => _entities.Keys
                .Where(key => key.PartitionKey == partitionKey && key.RowKey != AzureTableJournalStorage.HeaderRowKey)
                .Select(static key => key.RowKey)
                .ToList();

        public object? GetProperty(string partitionKey, string rowKey, string propertyName)
            => _entities[(partitionKey, rowKey)].Properties.TryGetValue(propertyName, out var value) ? value : null;

        public void PutEntity(string partitionKey, string rowKey, Dictionary<string, object> properties)
            => _entities[(partitionKey, rowKey)] = new StoredEntity(
                new Dictionary<string, object>(properties),
                new ETag($"put-{++_putCount}"));

        public void RemoveRow(string partitionKey, string rowKey) => _entities.Remove((partitionKey, rowKey));

        private Task<Response> AddEntityAsync(ITableEntity entity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = ExtractProperties(entity);
            AddCalls.Add(new(entity.PartitionKey, entity.RowKey, properties));
            if (_entities.ContainsKey((entity.PartitionKey, entity.RowKey)))
            {
                throw new RequestFailedException(409, "The specified entity already exists.", "EntityAlreadyExists", null);
            }

            var eTag = new ETag($"add-{++_addCount}");
            _entities[(entity.PartitionKey, entity.RowKey)] = new StoredEntity(properties, eTag);
            return Task.FromResult<Response>(new FakeResponse(eTag));
        }

        private Task<Response<T>> GetEntityAsync<T>(string partitionKey, string rowKey, CancellationToken cancellationToken)
            where T : ITableEntity
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls.Add(new(partitionKey, rowKey));
            if (!_entities.TryGetValue((partitionKey, rowKey), out var stored))
            {
                throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
            }

            return Task.FromResult(Response.FromValue((T)(object)CreateEntity(partitionKey, rowKey, stored), new FakeResponse(stored.ETag)));
        }

        private Task<Response> UpdateEntityAsync(ITableEntity entity, ETag ifMatch, TableUpdateMode mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = ExtractProperties(entity);
            UpdateCalls.Add(new(entity.PartitionKey, entity.RowKey, ifMatch, mode, properties));
            BeforeUpdate?.Invoke(entity.PartitionKey, entity.RowKey);
            var eTag = ApplyUpdate(entity.PartitionKey, entity.RowKey, properties, ifMatch, mode, new ETag($"update-{++_updateCount}"));
            return Task.FromResult<Response>(new FakeResponse(eTag));
        }

        private Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls.Add(new(partitionKey, rowKey, ifMatch));
            if (!_entities.TryGetValue((partitionKey, rowKey), out var stored))
            {
                // The table client swallows 404 responses to make deletes idempotent.
                return Task.FromResult<Response>(new FakeResponse(eTag: default));
            }

            if (ifMatch != default && ifMatch != ETag.All && ifMatch != stored.ETag)
            {
                throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
            }

            _entities.Remove((partitionKey, rowKey));
            return Task.FromResult<Response>(new FakeResponse(eTag: default));
        }

        private Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actions = transactionActions.ToList();
            TransactionCalls.Add(actions
                .Select(action => new TransactionAction(
                    action.ActionType,
                    action.Entity.PartitionKey,
                    action.Entity.RowKey,
                    action.ETag,
                    ExtractProperties(action.Entity)))
                .ToList());

            if (actions.Count is 0 or > 100)
            {
                throw new RequestFailedException(400, "The batch request operation exceeds the maximum 100 changes per change set.", "InvalidInput", null);
            }

            if (FailNextTransaction)
            {
                FailNextTransaction = false;
                throw new RequestFailedException(500, "Transaction failed.");
            }

            // Validate every action before applying any so the transaction is atomic.
            foreach (var action in actions)
            {
                var key = (action.Entity.PartitionKey, action.Entity.RowKey);
                var exists = _entities.TryGetValue(key, out var stored);
                switch (action.ActionType)
                {
                    case TableTransactionActionType.Add when exists:
                        throw new RequestFailedException(409, "The specified entity already exists.", "EntityAlreadyExists", null);
                    case TableTransactionActionType.UpdateMerge or TableTransactionActionType.UpdateReplace or TableTransactionActionType.Delete when !exists:
                        throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
                    case TableTransactionActionType.UpdateMerge or TableTransactionActionType.UpdateReplace or TableTransactionActionType.Delete
                        when action.ETag != default && action.ETag != ETag.All && action.ETag != stored!.ETag:
                        throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
                    case TableTransactionActionType.UpsertMerge or TableTransactionActionType.UpsertReplace:
                        throw new NotSupportedException("The fake table store does not implement upserts.");
                }
            }

            var eTag = new ETag($"txn-{++_transactionCount}");
            var responses = new List<Response>(actions.Count);
            foreach (var action in actions)
            {
                var key = (action.Entity.PartitionKey, action.Entity.RowKey);
                switch (action.ActionType)
                {
                    case TableTransactionActionType.Add:
                        _entities[key] = new StoredEntity(ExtractProperties(action.Entity), eTag);
                        responses.Add(new FakeResponse(eTag));
                        break;
                    case TableTransactionActionType.UpdateMerge or TableTransactionActionType.UpdateReplace:
                        ApplyUpdate(
                            action.Entity.PartitionKey,
                            action.Entity.RowKey,
                            ExtractProperties(action.Entity),
                            action.ETag,
                            action.ActionType is TableTransactionActionType.UpdateMerge ? TableUpdateMode.Merge : TableUpdateMode.Replace,
                            eTag);
                        responses.Add(new FakeResponse(eTag));
                        break;
                    case TableTransactionActionType.Delete:
                        _entities.Remove(key);
                        responses.Add(new FakeResponse(eTag: default));
                        break;
                }
            }

            return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>(responses, new FakeResponse(eTag: default)));
        }

        private AsyncPageable<T> Query<T>(string? filter, CancellationToken cancellationToken)
            where T : ITableEntity
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls.Add(new(filter));
            var predicate = FilterParser.Parse(filter);
            var results = _entities
                .Where(pair => predicate(pair.Key.PartitionKey, pair.Key.RowKey))
                .Select(pair => (T)(object)CreateEntity(pair.Key.PartitionKey, pair.Key.RowKey, pair.Value))
                .ToList();
            var pages = results
                .Chunk(PageSize)
                .Select(page => Page<T>.FromValues(page, continuationToken: null, new FakeResponse(eTag: default)))
                .ToList();
            if (pages.Count == 0)
            {
                pages.Add(Page<T>.FromValues([], continuationToken: null, new FakeResponse(eTag: default)));
            }

            return AsyncPageable<T>.FromPages(pages);
        }

        private ETag ApplyUpdate(
            string partitionKey,
            string rowKey,
            Dictionary<string, object> properties,
            ETag ifMatch,
            TableUpdateMode mode,
            ETag newETag)
        {
            if (!_entities.TryGetValue((partitionKey, rowKey), out var stored))
            {
                throw new RequestFailedException(404, "The specified resource does not exist.", "ResourceNotFound", null);
            }

            if (ifMatch != default && ifMatch != ETag.All && ifMatch != stored.ETag)
            {
                throw new RequestFailedException(412, "The update condition specified in the request was not satisfied.", "UpdateConditionNotSatisfied", null);
            }

            Dictionary<string, object> updated;
            if (mode is TableUpdateMode.Replace)
            {
                updated = properties;
            }
            else
            {
                updated = new Dictionary<string, object>(stored.Properties);
                foreach (var (name, value) in properties)
                {
                    updated[name] = value;
                }
            }

            _entities[(partitionKey, rowKey)] = new StoredEntity(updated, newETag);
            return newETag;
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

        private static Dictionary<string, object> ExtractProperties(ITableEntity entity)
        {
            var tableEntity = (TableEntity)entity;
            var result = new Dictionary<string, object>();
            foreach (var (name, value) in tableEntity)
            {
                if (name is not (nameof(ITableEntity.PartitionKey) or nameof(ITableEntity.RowKey) or nameof(ITableEntity.Timestamp) or "odata.etag"))
                {
                    result[name] = value;
                }
            }

            return result;
        }

        private sealed record StoredEntity(Dictionary<string, object> Properties, ETag ETag);

        private sealed class KeyComparer : IComparer<(string PartitionKey, string RowKey)>
        {
            public static KeyComparer Instance { get; } = new();

            public int Compare((string PartitionKey, string RowKey) x, (string PartitionKey, string RowKey) y)
            {
                var result = string.CompareOrdinal(x.PartitionKey, y.PartitionKey);
                return result != 0 ? result : string.CompareOrdinal(x.RowKey, y.RowKey);
            }
        }

        private sealed class FakeTableClient(FakeTableStore store) : TableClient
        {
            public override string Name => "journal";

            public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
                => store.AddEntityAsync(entity, cancellationToken);

            public override Task<Response<T>> GetEntityAsync<T>(string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
                => store.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken);

            public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
                => store.UpdateEntityAsync(entity, ifMatch, mode, cancellationToken);

            public override Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch = default, CancellationToken cancellationToken = default)
                => store.DeleteEntityAsync(partitionKey, rowKey, ifMatch, cancellationToken);

            public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
                => store.SubmitTransactionAsync(transactionActions, cancellationToken);

            public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
                => store.Query<T>(filter, cancellationToken);
        }
    }

    /// <summary>
    /// Evaluates the OData filters the storage implementation generates: conjunctions of
    /// <c>PartitionKey</c>/<c>RowKey</c> comparisons against quoted string literals.
    /// </summary>
    private static class FilterParser
    {
        public static Func<string, string, bool> Parse(string? filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return static (_, _) => true;
            }

            var comparisons = filter.Split(" and ", StringSplitOptions.None).Select(ParseComparison).ToList();
            return (partitionKey, rowKey) => comparisons.All(comparison => comparison(partitionKey, rowKey));
        }

        private static Func<string, string, bool> ParseComparison(string comparison)
        {
            var parts = comparison.Split(' ', 3);
            Assert.Equal(3, parts.Length);
            var property = parts[0];
            var op = parts[1];
            var literal = parts[2];
            Assert.StartsWith("'", literal);
            Assert.EndsWith("'", literal);
            var value = literal[1..^1].Replace("''", "'");

            Func<string, string, string> select = property switch
            {
                nameof(ITableEntity.PartitionKey) => static (partitionKey, _) => partitionKey,
                nameof(ITableEntity.RowKey) => static (_, rowKey) => rowKey,
                _ => throw new NotSupportedException($"The fake table store does not support filtering on '{property}'."),
            };

            return op switch
            {
                "eq" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) == 0,
                "ne" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) != 0,
                "ge" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) >= 0,
                "gt" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) > 0,
                "le" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) <= 0,
                "lt" => (partitionKey, rowKey) => string.CompareOrdinal(select(partitionKey, rowKey), value) < 0,
                _ => throw new NotSupportedException($"The fake table store does not support the '{op}' operator."),
            };
        }
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
