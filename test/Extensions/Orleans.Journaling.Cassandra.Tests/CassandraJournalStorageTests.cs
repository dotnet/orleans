using System.Buffers;
using System.Reflection;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Journaling;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Journaling")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
public sealed class CassandraJournalStorageTests : IClassFixture<CassandraJournalContainer>
{
    private readonly CassandraJournalContainer _container;

    public CassandraJournalStorageTests(CassandraJournalContainer container)
    {
        _container = container;
        _container.EnsurePreconditionsMet();
    }

    [Fact]
    public async Task AppendAsync_RoundTripsPagedRowsAndRequestsCompaction()
    {
        await using var harness = await CreateHarnessAsync(compactionAppendCount: 2, readPageSize: 2);
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);

        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "alice" },
            TestContext.Current.CancellationToken));
        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), TestContext.Current.CancellationToken);
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken);
        await storage.AppendAsync(new ReadOnlySequence<byte>([4, 5]), TestContext.Current.CancellationToken);

        Assert.True(storage.IsCompactionRequested);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4, 5], consumer.Bytes.ToArray());
        Assert.Equal("json", consumer.FormatKey);
        Assert.True(consumer.IsCompleted);
        Assert.Equal("alice", consumer.Metadata!.Properties["owner"]);
    }

    [Fact]
    public async Task ReadAsync_WhenJournalDoesNotExist_CompletesEmpty()
    {
        await using var harness = await CreateHarnessAsync();
        var consumer = new CapturingConsumer();

        await harness.Provider.CreateStorage(NewJournalId()).ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.Empty(consumer.Bytes.ToArray());
        Assert.Null(consumer.Metadata);
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public async Task AppendAsync_RejectsJournalCreatedAfterThisHandleObservedAbsence()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        var consumer = new CapturingConsumer();
        await staleStorage.ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.True(consumer.IsCompleted);

        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask());

        consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_RejectsJournalCreatedAfterThisHandleObservedAbsence()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        var consumer = new CapturingConsumer();
        await staleStorage.ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.True(consumer.IsCompleted);

        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask());

        consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAndReplaceAsync_ColdHandlesUseCurrentJournal()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);

        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([2]),
            TestContext.Current.CancellationToken);
        await harness.Provider.CreateStorage(id).ReplaceAsync(
            new ReadOnlySequence<byte>([3]),
            TestContext.Current.CancellationToken);

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([3], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_AppliesMetadataOnlyOnCreation()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);

        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "first" },
            TestContext.Current.CancellationToken));
        Assert.False(await harness.Provider.CreateStorage(id).CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "second" },
            TestContext.Current.CancellationToken));

        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await harness.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal("first", metadata.Properties["owner"]);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Empty(consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAsync_EmptySequenceDoesNotCreateOrChangeJournal()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);

        await storage.AppendAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await storage.AppendAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken);

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task AppendAsync_UpdatesFormatWhenAppendingToAnEmptyJournalAfterFormatChange()
    {
        await using var oldFormatHarness = await CreateHarnessAsync(journalFormatKey: "old-format");
        await using var newFormatHarness = await CreateHarnessAsync(
            journalFormatKey: "new-format",
            tableName: oldFormatHarness.Options.TableName);
        var id = NewJournalId();
        Assert.True(await oldFormatHarness.Provider.CreateStorage(id).CreateIfNotExistsAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        await newFormatHarness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1, 2]),
            TestContext.Current.CancellationToken);

        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(
            await newFormatHarness.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Equal("new-format", metadata.FormatKey);
        var consumer = new CapturingConsumer();
        await newFormatHarness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal("new-format", consumer.FormatKey);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_PublishesFreshGenerationWithRestartedSequenceAndRemovesOldRows()
    {
        await using var harness = await CreateHarnessAsync(rowSizeBytes: 4, readPageSize: 2);
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), TestContext.Current.CancellationToken);
        var oldGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);

        var replacement = Enumerable.Range(10, 9).Select(value => (byte)value).ToArray();
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(replacement), TestContext.Current.CancellationToken);

        var newGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.NotEqual(oldGeneration, newGeneration);
        var newGenerationSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, newGeneration);
        Assert.Equal([0L, 1L, 2L], newGenerationSequences);
        Assert.Empty(await GetSequencesAsync(harness.Session, harness.Options.TableName, id, oldGeneration));

        await storage.AppendAsync(new ReadOnlySequence<byte>([19]), TestContext.Current.CancellationToken);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([.. replacement, 19], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_StagesMultipleBatchesAndRecoversAllRows()
    {
        await using var harness = await CreateHarnessAsync(rowSizeBytes: 1);
        var id = NewJournalId();
        var expected = Enumerable.Range(0, 65).Select(static value => (byte)value).ToArray();

        await harness.Provider.CreateStorage(id).ReplaceAsync(
            new ReadOnlySequence<byte>(expected),
            TestContext.Current.CancellationToken);

        var generation = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        var sequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, generation);
        Assert.True(Enumerable.Range(0, 65).Select(static value => (long)value).SequenceEqual(sequences));
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal(expected, consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public async Task ReplaceAsync_WhenSecondStagingBatchFails_KeepsOldGenerationAndDeletesStagedRows()
    {
        FailSecondStagingBatchSessionProxy? stagingProxy = null;
        await using var harness = await CreateHarnessAsync(
            rowSizeBytes: 1,
            sessionDecorator: session => FailSecondStagingBatchSessionProxy.Create(session, out stagingProxy));
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([99]), TestContext.Current.CancellationToken);
        var oldGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        stagingProxy!.Arm();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReplaceAsync(
                new ReadOnlySequence<byte>(Enumerable.Range(0, 65).Select(static value => (byte)value).ToArray()),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Injected failure on the second replacement staging batch.", exception.Message);
        Assert.Equal(2, stagingProxy.AttemptedStagingBatches);
        Assert.Equal(1, stagingProxy.SucceededStagingBatches);
        Assert.Equal(oldGeneration, await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id));
        var oldGenerationSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, oldGeneration);
        Assert.Equal([0L], oldGenerationSequences);
        var generations = await GetGenerationsAsync(harness.Session, harness.Options.TableName, id);
        Assert.True(new HashSet<Guid> { Guid.Empty, oldGeneration }.SetEquals(generations));
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([99], consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public async Task ReplaceAsync_PreservesSegmentedInputAcrossRowBoundaries()
    {
        await using var harness = await CreateHarnessAsync(rowSizeBytes: 6);
        var id = NewJournalId();
        var expected = Enumerable.Range(1, 37).Select(value => (byte)value).ToArray();
        var value = CreateSegmentedSequence(expected, [1, 3, 2, 7, 1, 5, 4, 9, 5]);

        await harness.Provider.CreateStorage(id).ReplaceAsync(value, TestContext.Current.CancellationToken);

        var generation = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.Equal(7, (await GetSequencesAsync(harness.Session, harness.Options.TableName, id, generation)).Length);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal(expected, consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_EmptySnapshotPublishesEmptyGenerationAndRemainsAppendable()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var previousGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);

        await storage.ReplaceAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken);

        var generation = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.NotEqual(previousGeneration, generation);
        Assert.Empty(await GetSequencesAsync(harness.Session, harness.Options.TableName, id, generation));
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Empty(consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReadAsync_WhenActiveEntryIsMissing_RequiresRecovery()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);
        var generation = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        await harness.Session.ExecuteAsync(new SimpleStatement(
            $"DELETE FROM {harness.Options.TableName} WHERE journal_id = ? AND generation = ? AND sequence = ?",
            id.Value,
            generation,
            0L));
        var consumer = new CapturingConsumer();

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(consumer.Bytes.ToArray());
        Assert.False(consumer.IsCompleted);
    }

    [Fact]
    public async Task ReadAsync_PreservesRecordsSplitAcrossRowsAndPages()
    {
        await using var harness = await CreateHarnessAsync(rowSizeBytes: 5, readPageSize: 1);
        var id = NewJournalId();
        var expected = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
        await harness.Provider.CreateStorage(id).ReplaceAsync(
            new ReadOnlySequence<byte>(expected),
            TestContext.Current.CancellationToken);

        var consumer = new FramedRecordConsumer(recordLength: 12);
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.True(consumer.IsCompleted);
        Assert.Equal(2, consumer.Records.Count);
        Assert.Equal(expected, consumer.Records.SelectMany(record => record).ToArray());
    }

    [Fact]
    public async Task ReadAsync_CanBeCancelledWhileFetchingTheNextPage()
    {
        DelayedPageSessionProxy? pagingSession = null;
        await using var harness = await CreateHarnessAsync(
            rowSizeBytes: 1,
            readPageSize: 1,
            sessionDecorator: session =>
            {
                return DelayedPageSessionProxy.Create(session, out pagingSession);
            });
        var id = NewJournalId();
        await harness.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
#pragma warning disable xUnit1051 // The linked token is canceled by this test to exercise paging.
        var readTask = Task.Run(async () =>
            await harness.Provider.CreateStorage(id).ReadAsync(new CapturingConsumer(), cancellation.Token));
#pragma warning restore xUnit1051
        try
        {
            await pagingSession!.PageFetchStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();

            var completedTask = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
            Assert.True(ReferenceEquals(readTask, completedTask), "ReadAsync did not stop waiting for the Cassandra page after cancellation.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        }
        finally
        {
            pagingSession!.ReleasePageFetch();
            try
            {
                await readTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task ReplaceAsync_DeletesGenerationsOlderThanTheCleanupCutoff()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        var staleGeneration = CassandraJournalGenerationId.Create(DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal('7', staleGeneration.ToString("N")[12]);
        await harness.Session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {harness.Options.TableName} (journal_id, generation, sequence, operation_id, payload) VALUES (?, ?, ?, ?, ?)",
            id.Value,
            staleGeneration,
            0L,
            Guid.NewGuid(),
            new byte[] { 99 }));
        var recentGeneration = CassandraJournalGenerationId.Create(DateTimeOffset.UtcNow);
        await harness.Session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {harness.Options.TableName} (journal_id, generation, sequence, operation_id, payload) VALUES (?, ?, ?, ?, ?)",
            id.Value,
            recentGeneration,
            0L,
            Guid.NewGuid(),
            new byte[] { 100 }));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        var activeGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.Equal('7', activeGeneration.ToString("N")[12]);
        Assert.Empty(await GetSequencesAsync(harness.Session, harness.Options.TableName, id, staleGeneration));
        var recentSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, recentGeneration);
        Assert.Equal([0L], recentSequences);
        var activeSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, activeGeneration);
        Assert.Equal([0L], activeSequences);
    }

    [Fact]
    public async Task ReplaceAsync_ReturnsSuccessWhenPostPublicationCleanupFails()
    {
        await using var harness = await CreateHarnessAsync(
            sessionDecorator: static session => FailNextDeleteSessionProxy.Create(session));
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var previousGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);

        var staleGeneration = CassandraJournalGenerationId.Create(DateTimeOffset.UtcNow.AddDays(-2));
        await harness.Session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {harness.Options.TableName} (journal_id, generation, sequence, operation_id, payload) VALUES (?, ?, ?, ?, ?)",
            id.Value,
            staleGeneration,
            0L,
            Guid.NewGuid(),
            new byte[] { 99 }));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        var activeGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.NotEqual(previousGeneration, activeGeneration);
        var previousSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, previousGeneration);
        Assert.Equal([0L], previousSequences);
        Assert.Empty(await GetSequencesAsync(harness.Session, harness.Options.TableName, id, staleGeneration));
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_PreservesPublishedGenerationWhenResponseIsLost()
    {
        await using var harness = await CreateHarnessAsync(
            sessionDecorator: static session => PublicationResponseLossSessionProxy.Create(session));
        var id = NewJournalId();
        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Provider.CreateStorage(id).ReplaceAsync(
                new ReadOnlySequence<byte>([2, 3]),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("Injected lost response after replacement publication.", exception.Message);

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2, 3], consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);

        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([4]),
            TestContext.Current.CancellationToken);
        consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2, 3, 4], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task UpdateMetadataAsync_UsesLwtEtagAndPreservesAppendState()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        var original = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        var updated = Assert.IsAssignableFrom<IJournalMetadata>(await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "closed" },
            expectedETag: original.ETag,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotEqual(original.ETag, updated.ETag);
        Assert.Null(await storage.UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["catalog"] = "stale" },
            expectedETag: original.ETag,
            cancellationToken: TestContext.Current.CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
        Assert.Equal("closed", consumer.Metadata!.Properties["catalog"]);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentStaleWriterFailsLwtAndRequiresRecovery()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var seed = harness.Provider.CreateStorage(id);
        await seed.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var first = harness.Provider.CreateStorage(id);
        var second = harness.Provider.CreateStorage(id);
        await RecoverAsync(first);
        await RecoverAsync(second);

        await first.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => second.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_RejectsStalePublicationAndCleansStagedGeneration()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var seed = harness.Provider.CreateStorage(id);
        await seed.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        var replacer = harness.Provider.CreateStorage(id);
        var appender = harness.Provider.CreateStorage(id);
        await RecoverAsync(replacer);
        await RecoverAsync(appender);
        await appender.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => replacer.ReplaceAsync(new ReadOnlySequence<byte>([9]), TestContext.Current.CancellationToken).AsTask());

        var activeGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        var generations = await GetGenerationsAsync(harness.Session, harness.Options.TableName, id);
        Assert.Equal(2, generations.Length);
        Assert.Contains(activeGeneration, generations);
        var activeSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, activeGeneration);
        Assert.Equal([0L, 1L], activeSequences);
    }

    [Fact]
    public async Task DeleteAsync_HidesJournalAndRecreationStartsANewGeneration()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var deletedGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);

        await storage.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Empty(consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);

        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        var newGeneration = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);
        Assert.NotEqual(deletedGeneration, newGeneration);
        var newGenerationSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, newGeneration);
        Assert.Equal([0L], newGenerationSequences);
        Assert.Empty(await GetSequencesAsync(harness.Session, harness.Options.TableName, id, deletedGeneration));
    }

    [Fact]
    public async Task DeleteAsync_RejectsAStaleStorageHandle()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        await staleStorage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await RecoverAsync(staleStorage);

        var currentStorage = harness.Provider.CreateStorage(id);
        await currentStorage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RejectsAStaleStorageHandleAfterAppend()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        await staleStorage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await RecoverAsync(staleStorage);

        await harness.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RejectsAStorageHandleWhichPreviouslyObservedNoJournal()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        Assert.Null(await staleStorage.GetMetadataAsync(TestContext.Current.CancellationToken));

        await harness.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_RejectsAStaleStorageHandleAfterDeleteAndRecreate()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        await staleStorage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await RecoverAsync(staleStorage);

        await harness.Provider.CreateStorage(id).DeleteAsync(TestContext.Current.CancellationToken);
        await harness.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => staleStorage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_AllowsMetadataOnlyChangeSinceRecovery()
    {
        await using var harness = await CreateHarnessAsync();
        var id = NewJournalId();
        var staleStorage = harness.Provider.CreateStorage(id);
        await staleStorage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await RecoverAsync(staleStorage);

        await harness.Provider.CreateStorage(id).UpdateMetadataAsync(
            set: new Dictionary<string, string> { ["updated"] = "true" },
            cancellationToken: TestContext.Current.CancellationToken);

        await staleStorage.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Null(await harness.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_LwtConflictPreservesContentWrittenAfterHeaderRead()
    {
        DeleteHeaderRaceSessionProxy? raceProxy = null;
        await using var harness = await CreateHarnessAsync(
            sessionDecorator: session => DeleteHeaderRaceSessionProxy.Create(session, out raceProxy));
        var id = NewJournalId();
        await harness.Provider.CreateStorage(id).AppendAsync(
            new ReadOnlySequence<byte>([1]),
            TestContext.Current.CancellationToken);
        var appender = harness.Provider.CreateStorage(id);
        raceProxy!.BeforeDeleteAsync = () => appender.AppendAsync(
            new ReadOnlySequence<byte>([2]),
            TestContext.Current.CancellationToken).AsTask();

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => harness.Provider.CreateStorage(id).DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task DeleteAsync_WhenGenerationCleanupFails_ReportsFailureAfterHidingJournal()
    {
        await using var harness = await CreateHarnessAsync(
            sessionDecorator: static session => FailGenerationDeleteSessionProxy.Create(session));
        var id = NewJournalId();
        var storage = harness.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var generation = await GetActiveGenerationAsync(harness.Session, harness.Options.TableName, id);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("generation cleanup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await harness.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken));
        var survivingSequences = await GetSequencesAsync(harness.Session, harness.Options.TableName, id, generation);
        Assert.Equal([0L], survivingSequences);
        var consumer = new CapturingConsumer();
        await harness.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Empty(consumer.Bytes.ToArray());
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public async Task AppendAsync_RejectsOversizedRequestBeforeWriting()
    {
        await using var harness = await CreateHarnessAsync(maxAppendBytes: 3);
        var storage = harness.Provider.CreateStorage(NewJournalId());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3, 4]), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    private async Task<ProviderHarness> CreateHarnessAsync(
        int maxAppendBytes = 2 * 1024 * 1024,
        int rowSizeBytes = 64 * 1024,
        int compactionAppendCount = 100,
        int readPageSize = 128,
        Func<ISession, ISession>? sessionDecorator = null,
        string journalFormatKey = "json",
        string? tableName = null)
    {
        var session = await _container.GetSessionAsync(TestContext.Current.CancellationToken);
        var options = new CassandraJournalStorageOptions
        {
            TableName = tableName ?? $"journal_test_{Guid.NewGuid():N}",
            MaxAppendBytes = maxAppendBytes,
            RowSizeBytes = rowSizeBytes,
            CompactionAppendCount = compactionAppendCount,
            ReadPageSize = readPageSize,
        };
        options.ConfigureClient(_ => Task.FromResult(sessionDecorator?.Invoke(session) ?? session));
        var provider = new CassandraJournalStorageProvider(
            Options.Create(options),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CassandraJournalStorage>.Instance);
        await provider.InitializeAsync(TestContext.Current.CancellationToken);
        return new ProviderHarness(provider, options, session);
    }

    public class FailNextDeleteSessionProxy : DispatchProxy
    {
        private ISession? _inner;
        private int _failNextDelete;

        public static ISession Create(ISession inner)
        {
            var session = DispatchProxy.Create<ISession, FailNextDeleteSessionProxy>();
            var proxy = (FailNextDeleteSessionProxy)(object)session;
            proxy._inner = inner;
            proxy._failNextDelete = 1;
            return session;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BoundStatement statement
                && statement.PreparedStatement.QueryString.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _failNextDelete, 0) == 1)
            {
                throw new InvalidOperationException("Injected failure for the first generation delete.");
            }

            return targetMethod!.Invoke(_inner, args);
        }
    }

    private class FailGenerationDeleteSessionProxy : DispatchProxy
    {
        private ISession? _inner;

        public static ISession Create(ISession inner)
        {
            var session = DispatchProxy.Create<ISession, FailGenerationDeleteSessionProxy>();
            ((FailGenerationDeleteSessionProxy)(object)session)._inner = inner;
            return session;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BoundStatement statement)
            {
                var query = statement.PreparedStatement.QueryString;
                if (query.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                    && !query.Contains("IF revision", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Injected generation cleanup failure.");
                }
            }

            return targetMethod!.Invoke(_inner, args);
        }
    }

    private class DeleteHeaderRaceSessionProxy : DispatchProxy
    {
        private ISession? _inner;
        private int _racePending;

        public Func<Task>? BeforeDeleteAsync { get; set; }

        public static ISession Create(ISession inner, out DeleteHeaderRaceSessionProxy proxy)
        {
            var session = DispatchProxy.Create<ISession, DeleteHeaderRaceSessionProxy>();
            proxy = (DeleteHeaderRaceSessionProxy)(object)session;
            proxy._inner = inner;
            proxy._racePending = 1;
            return session;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BoundStatement statement
                && statement.PreparedStatement.QueryString.Contains("IF revision", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _racePending, 0) == 1)
            {
                return ExecuteAfterRaceAsync(statement);
            }

            return targetMethod!.Invoke(_inner, args);
        }

        private async Task<RowSet> ExecuteAfterRaceAsync(BoundStatement statement)
        {
            var beforeDelete = BeforeDeleteAsync
                ?? throw new InvalidOperationException("A delete race callback was not configured.");
            await beforeDelete().ConfigureAwait(false);
            return await _inner!.ExecuteAsync(statement).ConfigureAwait(false);
        }
    }

    private class PublicationResponseLossSessionProxy : DispatchProxy
    {
        private ISession? _inner;
        private int _loseNextPublicationResponse = 1;

        public static ISession Create(ISession inner)
        {
            var session = DispatchProxy.Create<ISession, PublicationResponseLossSessionProxy>();
            ((PublicationResponseLossSessionProxy)(object)session)._inner = inner;
            return session;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BoundStatement statement
                && statement.PreparedStatement.QueryString.Contains("SET active_generation", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _loseNextPublicationResponse, 0) == 1)
            {
                return ExecuteAndLoseResponseAsync(statement);
            }

            return targetMethod!.Invoke(_inner, args);
        }

        private async Task<RowSet> ExecuteAndLoseResponseAsync(BoundStatement statement)
        {
            await _inner!.ExecuteAsync(statement).ConfigureAwait(false);
            throw new InvalidOperationException("Injected lost response after replacement publication.");
        }
    }

    private class FailSecondStagingBatchSessionProxy : DispatchProxy
    {
        private ISession? _inner;
        private int _armed;
        private int _attemptedStagingBatches;
        private int _succeededStagingBatches;

        public int AttemptedStagingBatches => Volatile.Read(ref _attemptedStagingBatches);
        public int SucceededStagingBatches => Volatile.Read(ref _succeededStagingBatches);

        public static ISession Create(ISession inner, out FailSecondStagingBatchSessionProxy proxy)
        {
            var session = DispatchProxy.Create<ISession, FailSecondStagingBatchSessionProxy>();
            proxy = (FailSecondStagingBatchSessionProxy)(object)session;
            proxy._inner = inner;
            return session;
        }

        public void Arm() => Volatile.Write(ref _armed, 1);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Volatile.Read(ref _armed) == 1
                && targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BatchStatement)
            {
                var attempt = Interlocked.Increment(ref _attemptedStagingBatches);
                if (attempt == 2)
                {
                    return Task.FromException<RowSet>(
                        new InvalidOperationException("Injected failure on the second replacement staging batch."));
                }

                return ExecuteStagingBatchAsync(targetMethod, args);
            }

            return targetMethod!.Invoke(_inner, args);
        }

        private async Task<RowSet> ExecuteStagingBatchAsync(MethodInfo targetMethod, object?[] args)
        {
            var result = await ((Task<RowSet>)targetMethod.Invoke(_inner, args)!).ConfigureAwait(false);
            Interlocked.Increment(ref _succeededStagingBatches);
            return result;
        }
    }

    private class DelayedPageSessionProxy : DispatchProxy
    {
        private static readonly MethodInfo SetFetchNextPageHandler = typeof(RowSet).GetMethod(
            "SetFetchNextPageHandler",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        private ISession? _inner;
        private RowSet? _delayedRows;
        private readonly TaskCompletionSource _pageFetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RowSet> _pageFetchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PageFetchStarted => _pageFetchStarted.Task;

        public static ISession Create(ISession inner, out DelayedPageSessionProxy proxy)
        {
            var session = DispatchProxy.Create<ISession, DelayedPageSessionProxy>();
            proxy = (DelayedPageSessionProxy)(object)session;
            proxy._inner = inner;
            return session;
        }

        public void ReleasePageFetch()
            => _pageFetchRelease.TrySetResult(new RowSet());

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.ExecuteAsync)
                && args is { Length: > 0 }
                && args[0] is BoundStatement statement
                && statement.PreparedStatement.QueryString.Contains("SELECT sequence, payload", StringComparison.Ordinal))
            {
                _delayedRows ??= CreateDelayedRows();
                return Task.FromResult(_delayedRows);
            }

            return targetMethod!.Invoke(_inner, args);
        }

        private RowSet CreateDelayedRows()
            => new DelayedPageRowSet(
                new JournalEntryRow(0, [1]),
                FetchNextPageAsync);

        private async Task<RowSet> FetchNextPageAsync()
        {
            _pageFetchStarted.TrySetResult();
            return await _pageFetchRelease.Task.ConfigureAwait(false);
        }

        private sealed class DelayedPageRowSet : RowSet
        {
            public DelayedPageRowSet(Row row, Func<Task<RowSet>> fetchNextPage)
            {
                RowQueue.Enqueue(row);
                typeof(RowSet).GetProperty("PagingState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .SetValue(this, new byte[] { 1 });
                typeof(RowSet).GetProperty("AutoPage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .SetValue(this, true);
                // Hold the driver's next-page task open to model a delayed server response.
                SetFetchNextPageHandler.Invoke(this,
                [
                    new Func<byte[], Task<RowSet>>(_ => fetchNextPage()),
                    30_000,
                    null,
                ]);
            }
        }
    }

    private sealed class JournalEntryRow(long sequence, byte[] payload) : Row
    {
        public override T GetValue<T>(string name)
            => name switch
            {
                "sequence" => (T)(object)sequence,
                "payload" => (T)(object)payload,
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
    }

    private static async Task RecoverAsync(IJournalStorage storage)
        => await storage.ReadAsync(new CapturingConsumer(), TestContext.Current.CancellationToken);

    private static JournalId NewJournalId() => new($"test/{Guid.NewGuid():N}");

    private static ReadOnlySequence<byte> CreateSegmentedSequence(byte[] value, int[] segmentLengths)
    {
        var first = new TestSequenceSegment(value.AsMemory(0, segmentLengths[0]));
        var last = first;
        var offset = segmentLengths[0];
        foreach (var segmentLength in segmentLengths.Skip(1))
        {
            last = last.Append(value.AsMemory(offset, segmentLength));
            offset += segmentLength;
        }

        Assert.Equal(value.Length, offset);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static async Task<Guid> GetActiveGenerationAsync(ISession session, string table, JournalId id)
    {
        var row = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT active_generation FROM {table} WHERE journal_id = ? AND generation = ? AND sequence = ?",
            id.Value,
            Guid.Empty,
            -1L))).FirstOrDefault();
        return row?.GetValue<Guid>("active_generation")
            ?? throw new InvalidOperationException("The journal header was not found.");
    }

    private static async Task<long[]> GetSequencesAsync(ISession session, string table, JournalId id, Guid generation)
    {
        var result = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT sequence FROM {table} WHERE journal_id = ? AND generation = ?",
            id.Value,
            generation));
        return result.Select(row => row.GetValue<long>("sequence")).Order().ToArray();
    }

    private static async Task<Guid[]> GetGenerationsAsync(ISession session, string table, JournalId id)
    {
        var result = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT generation FROM {table} WHERE journal_id = ?",
            id.Value));
        return result.Select(row => row.GetValue<Guid>("generation")).Distinct().ToArray();
    }

    private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSequenceSegment Append(ReadOnlyMemory<byte> nextMemory)
        {
            var next = new TestSequenceSegment(nextMemory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }

        public TestSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
    }

    private sealed class ProviderHarness(
        CassandraJournalStorageProvider provider,
        CassandraJournalStorageOptions options,
        ISession session) : IAsyncDisposable
    {
        public CassandraJournalStorageProvider Provider { get; } = provider;
        public CassandraJournalStorageOptions Options { get; } = options;
        public ISession Session { get; } = session;

        public async ValueTask DisposeAsync() => await Provider.CloseAsync(CancellationToken.None);
    }

    private sealed class CapturingConsumer : IJournalStorageConsumer
    {
        public MemoryStream Bytes { get; } = new();
        public string? FormatKey { get; private set; }
        public IJournalMetadata? Metadata { get; private set; }
        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (metadata is not null)
            {
                FormatKey ??= metadata.FormatKey;
                Metadata = metadata;
            }

            if (buffer.Length > 0)
            {
                var bytes = buffer.ToArray();
                Bytes.Write(bytes);
                buffer.Skip(bytes.Length);
            }

            IsCompleted |= buffer.IsCompleted;
        }
    }

    private sealed class FramedRecordConsumer(int recordLength) : IJournalStorageConsumer
    {
        public List<byte[]> Records { get; } = [];
        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length >= recordLength)
            {
                var record = new byte[recordLength];
                buffer.Read(record);
                Records.Add(record);
            }

            if (buffer.IsCompleted)
            {
                IsCompleted = true;
                if (buffer.Length != 0)
                {
                    throw new InvalidDataException("The journal ended with an incomplete record.");
                }
            }
        }
    }
}
