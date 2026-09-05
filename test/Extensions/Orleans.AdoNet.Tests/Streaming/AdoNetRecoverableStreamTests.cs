using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.AdoNet;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.Streams;
using Tester.AdoNet.Fakes;

namespace Tester.AdoNet.Streaming;

[TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class AdoNetRecoverableStreamTests
{
    [Fact]
    public async Task ConcurrentCreateAdapterCallsConstructOnce()
    {
        const int callerCount = 8;
        var lifetime = new FakeHostApplicationLifetime();
        var adapter = Substitute.For<IQueueAdapter>();
        var factory = new BlockingAdoNetQueueAdapterFactory(
            CreateQueries(new CapturingRelationalStorage()),
            adapter,
            lifetime);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var invoked = new CountdownEvent(callerCount);
        var tasks = Enumerable.Range(0, callerCount).Select(async _ =>
        {
            await start.Task;
            var operation = factory.CreateAdapter();
            invoked.Signal();
            return await operation;
        }).ToArray();

        start.SetResult();
        await Task.Run(
            () => invoked.Wait(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var adapterConstructionCount = factory.AdapterConstructionCount;
        factory.CompleteAdapterConstruction();

        var adapters = await Task.WhenAll(tasks);
        Assert.Equal(1, adapterConstructionCount);
        Assert.Equal(1, factory.AdapterConstructionCount);
        Assert.All(adapters, result => Assert.Same(adapter, result));
    }

    [Fact]
    public async Task TimedOutCreateAdapterDoesNotEnterCriticalSection()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var adapter = Substitute.For<IQueueAdapter>();
        var factory = new BlockingAdoNetQueueAdapterFactory(
            CreateQueries(new CapturingRelationalStorage()),
            adapter,
            lifetime,
            TimeSpan.FromMilliseconds(50));

        var first = factory.CreateAdapter();
        await factory.AdapterConstructionStarted.Task;

        await Assert.ThrowsAsync<TimeoutException>(() => factory.CreateAdapter());
        Assert.Equal(1, factory.AdapterConstructionCount);

        factory.CompleteAdapterConstruction();
        Assert.Same(adapter, await first);
        Assert.Same(adapter, await factory.CreateAdapter());
        Assert.Equal(1, factory.AdapterConstructionCount);
    }

    [Fact]
    public void ResolveCheckpointUpdate_ReturnsAuthoritativeStateForExpectedVersionConflict()
    {
        var update = new AdoNetStreamCheckpointUpdate(
            "service",
            "provider",
            "queue",
            OwnerEpoch: 7,
            Checkpoint: 42,
            Updated: false);

        var result = AdoNetRecoverableStream.ResolveCheckpointUpdate("service/provider/queue", 7, update);

        Assert.Equal("42", result.Checkpoint);
        Assert.Equal("7", result.Version);
    }

    [Fact]
    public void ResolveCheckpointUpdate_ThrowsWhenPartitionOwnershipIsLost()
    {
        var update = new AdoNetStreamCheckpointUpdate(
            "service",
            "provider",
            "queue",
            OwnerEpoch: 8,
            Checkpoint: 42,
            Updated: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AdoNetRecoverableStream.ResolveCheckpointUpdate("service/provider/queue", 7, update));

        Assert.Contains("ownership was lost", exception.Message);
        Assert.Contains("service/provider/queue", exception.Message);
        Assert.Contains("epoch 7", exception.Message);
    }

    [Theory]
    [InlineData(1.1, 2)]
    [InlineData(1.9, 2)]
    [InlineData(2.0, 2)]
    public void ToSqlSeconds_RoundsUpWithoutShorteningRetention(double seconds, int expected)
        => Assert.Equal(expected, AdoNetStreamTime.ToSqlSeconds(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ToSqlSeconds_ThrowsWhenCeilingExceedsSqlIntegerRange()
    {
        var value = TimeSpan.FromSeconds(int.MaxValue) + TimeSpan.FromTicks(1);

        Assert.Throws<OverflowException>(() => AdoNetStreamTime.ToSqlSeconds(value));
    }

    [Fact]
    public void CleanupParameters_UseRoundedIntegerSecondsAndNullableMaximum()
    {
        using var command = new SqlCommand();
        _ = new DbStoredQueries.Columns(command)
        {
            RetentionPeriodSeconds = AdoNetStreamTime.ToSqlSeconds(TimeSpan.FromSeconds(1.1)),
            MaximumRetentionPeriodSeconds = null,
            CleanupIntervalSeconds = AdoNetStreamTime.ToSqlSeconds(TimeSpan.FromSeconds(1.9)),
        };

        Assert.Equal(2, command.Parameters[nameof(DbStoredQueries.Columns.RetentionPeriodSeconds)].Value);
        var maximum = command.Parameters[nameof(DbStoredQueries.Columns.MaximumRetentionPeriodSeconds)];
        Assert.Equal(DBNull.Value, maximum.Value);
        Assert.Equal(DbType.Int32, maximum.DbType);
        Assert.Equal(2, command.Parameters[nameof(DbStoredQueries.Columns.CleanupIntervalSeconds)].Value);
    }

    [Fact]
    public async Task Load_PropagatesCancellationAndDiscardsLateAcquisition()
    {
        var storage = new BlockingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions { StartFromNow = false },
            CreateQueries(storage),
            NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();

        var loadTask = source.Load(cancellation.Token).AsTask();
        await storage.AcquisitionStarted.Task;
        cancellation.Cancel();

        Assert.True(storage.CapturedCancellationToken.IsCancellationRequested);
        Assert.False(source.AcquisitionCompletion.IsCompleted);

        storage.CompleteAcquisition(ownerEpoch: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loadTask);
        Assert.True(source.AcquisitionCompletion.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.Update("1", "1", TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(AcquisitionCompletionKind.Success)]
    [InlineData(AcquisitionCompletionKind.Fault)]
    [InlineData(AcquisitionCompletionKind.Canceled)]
    public async Task ShutdownNotification_WaitsForAnyAcquisitionCompletionBeforeAllowingReplacement(
        AcquisitionCompletionKind completionKind)
    {
        var queueId = QueueId.GetQueueId("queue", 0, 0);
        var registry = new QueueAdapterReceiverRegistry<ReservedReceiver>(_ => new ReservedReceiver());
        var first = registry.GetOrCreate(queueId);
        var acquisition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = AdoNetQueueAdapterReceiver.NotifyShutdownAfterAcquisition(
            acquisition.Task,
            () => registry.Remove(queueId, first));

        Assert.Same(first, registry.GetOrCreate(queueId));
        switch (completionKind)
        {
            case AcquisitionCompletionKind.Success:
                acquisition.SetResult();
                break;
            case AcquisitionCompletionKind.Fault:
                acquisition.SetException(new InvalidOperationException("late failure"));
                break;
            case AcquisitionCompletionKind.Canceled:
                acquisition.SetCanceled(TestContext.Current.CancellationToken);
                break;
        }

        await released;

        var replacement = registry.GetOrCreate(queueId);
        Assert.NotSame(first, replacement);
        Assert.Same(replacement, Assert.Single(registry.Receivers).Value);
    }

    [Theory]
    [InlineData(StreamQueryKind.Read)]
    [InlineData(StreamQueryKind.Advance)]
    [InlineData(StreamQueryKind.Cleanup)]
    [InlineData(StreamQueryKind.Bounds)]
    public async Task StreamingQueries_PropagateCancellationToken(StreamQueryKind queryKind)
    {
        var storage = new CapturingRelationalStorage();
        var queries = CreateQueries(storage);
        using var cancellation = new CancellationTokenSource();

        switch (queryKind)
        {
            case StreamQueryKind.Read:
                _ = await queries.ReadStreamMessagesAsync(
                    "service", "provider", "queue", 0, 1, cancellation.Token);
                break;
            case StreamQueryKind.Advance:
                _ = await queries.AdvanceStreamCheckpointAsync(
                    "service", "provider", "queue", 1, 1, cancellation.Token);
                break;
            case StreamQueryKind.Cleanup:
                _ = await queries.CleanupStreamMessagesAsync(
                    "service", "provider", "queue", 1, 2, 3, 4, cancellation.Token);
                break;
            case StreamQueryKind.Bounds:
                _ = await queries.GetStreamPartitionBoundsAsync(
                    "service", "provider", "queue", cancellation.Token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(queryKind));
        }

        Assert.Equal(cancellation.Token, storage.CapturedCancellationToken);
    }

    [Fact]
    public async Task Read_UsesDistinctRoundedRetentionParameters()
    {
        var storage = new CapturingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions
            {
                MaxMessagesPerRead = 10,
                RetentionPeriod = TimeSpan.FromSeconds(2.1),
                MaximumRetentionPeriod = TimeSpan.FromSeconds(5.1),
                CleanupInterval = TimeSpan.FromSeconds(3.1),
                CleanupBatchSize = 9,
            },
            CreateQueries(storage),
            NullLogger.Instance);

        Assert.Empty(await source.Read(10, TestContext.Current.CancellationToken));

        var parameters = storage.Parameters[nameof(DbStoredQueries.CleanupStreamMessagesKey)];
        Assert.Equal(3, parameters[nameof(DbStoredQueries.Columns.RetentionPeriodSeconds)]);
        Assert.Equal(6, parameters[nameof(DbStoredQueries.Columns.MaximumRetentionPeriodSeconds)]);
        Assert.Equal(4, parameters[nameof(DbStoredQueries.Columns.CleanupIntervalSeconds)]);
        Assert.Equal(9, parameters[nameof(DbStoredQueries.Columns.CleanupBatchSize)]);
    }

    [Fact]
    public async Task Read_HardRetentionPastReturnedBatchPermanentlyFaultsSource()
    {
        var storage = new CapturingRelationalStorage
        {
            ReadRecords =
            [
                Record(
                    (nameof(AdoNetStreamMessage.ServiceId), "service"),
                    (nameof(AdoNetStreamMessage.ProviderId), "provider"),
                    (nameof(AdoNetStreamMessage.QueueId), "queue"),
                    (nameof(AdoNetStreamMessage.MessageId), 1L),
                    (nameof(AdoNetStreamMessage.StreamIdBytes), new byte[] { 1 }),
                    (nameof(AdoNetStreamMessage.StreamNamespaceLength), 0),
                    (nameof(AdoNetStreamMessage.CreatedOn), DateTime.UtcNow),
                    (nameof(AdoNetStreamMessage.Payload), new byte[] { 2 })),
            ],
            CleanupRecords =
            [
                Record(
                    (nameof(AdoNetStreamCleanupResult.Ran), true),
                    (nameof(AdoNetStreamCleanupResult.DeletedCount), 100),
                    (nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId), 100L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedCount), 100),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), 100L),
                    (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                    (nameof(AdoNetStreamCleanupResult.EarliestMessageId), 101L),
                    (nameof(AdoNetStreamCleanupResult.TailMessageId), 101L)),
            ],
        };
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions { MaxMessagesPerRead = 10 },
            CreateQueries(storage),
            NullLogger.Instance);

        var first = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));
        var readsAfterFailure = storage.ReadCallCount;
        var second = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));

        Assert.Same(first, second);
        Assert.Contains("hard retention deleted through message 100", first.Message);
        Assert.Equal(readsAfterFailure, storage.ReadCallCount);
    }

    [Fact]
    public async Task MessagesAddFailed_AfterHardDeletionPermanentlyFaultsSource()
    {
        var storage = new CapturingRelationalStorage
        {
            ReadRecords =
            [
                Record(
                    (nameof(AdoNetStreamMessage.ServiceId), "service"),
                    (nameof(AdoNetStreamMessage.ProviderId), "provider"),
                    (nameof(AdoNetStreamMessage.QueueId), "queue"),
                    (nameof(AdoNetStreamMessage.MessageId), 1L),
                    (nameof(AdoNetStreamMessage.StreamIdBytes), new byte[] { 1 }),
                    (nameof(AdoNetStreamMessage.StreamNamespaceLength), 0),
                    (nameof(AdoNetStreamMessage.CreatedOn), DateTime.UtcNow),
                    (nameof(AdoNetStreamMessage.Payload), new byte[] { 2 })),
            ],
            CleanupRecords =
            [
                Record(
                    (nameof(AdoNetStreamCleanupResult.Ran), true),
                    (nameof(AdoNetStreamCleanupResult.DeletedCount), 1),
                    (nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedCount), 1),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                    (nameof(AdoNetStreamCleanupResult.EarliestMessageId), 2L),
                    (nameof(AdoNetStreamCleanupResult.TailMessageId), 2L)),
            ],
        };
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions { MaxMessagesPerRead = 10 },
            CreateQueries(storage),
            NullLogger.Instance);
        var messages = await source.Read(10, TestContext.Current.CancellationToken);

        source.MessagesAddFailed(messages);
        var readsAfterFailure = storage.ReadCallCount;
        var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));

        Assert.Contains("cache admission failed", failure.Message);
        Assert.Equal(readsAfterFailure, storage.ReadCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessagesAdded_PartialAdmissionPreservesHardDeletedSuffix(bool admitWholeBatch)
    {
        var storage = new CapturingRelationalStorage
        {
            ReadRecords = [MessageRecord(1), MessageRecord(2), MessageRecord(3)],
            CleanupRecords =
            [
                Record(
                    (nameof(AdoNetStreamCleanupResult.Ran), true),
                    (nameof(AdoNetStreamCleanupResult.DeletedCount), 3),
                    (nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId), 3L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedCount), 3),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), 3L),
                    (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                    (nameof(AdoNetStreamCleanupResult.EarliestMessageId), null),
                    (nameof(AdoNetStreamCleanupResult.TailMessageId), null)),
            ],
        };
        var source = CreateSource(storage);
        var messages = await source.Read(3, TestContext.Current.CancellationToken);
        storage.CleanupRecords = null;
        source.MessagesAdded([messages[0]]);
        source.MessagesAdded([messages[1]]);
        if (admitWholeBatch)
        {
            source.MessagesAdded([messages[2]]);
            source.MessagesAddFailed([]);
            storage.ReadRecords = [MessageRecord(4)];
            Assert.Equal(4, Assert.Single(await source.Read(1, TestContext.Current.CancellationToken)).MessageId);
        }
        else
        {
            source.MessagesAddFailed([messages[2]]);
            var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
                () => source.Read(1, TestContext.Current.CancellationToken));
            Assert.Contains("through message 3", failure.Message);
            Assert.Contains("cache admission failed", failure.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MissingFirstOrInteriorMessagePermanentlyFaultsSource(bool interiorGap)
    {
        var storage = new CapturingRelationalStorage
        {
            ReadRecords = interiorGap ? [MessageRecord(1), MessageRecord(3)] : [MessageRecord(2)],
        };
        var source = CreateSource(storage);

        var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));
        var calls = storage.ReadCallCount;
        var repeated = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));

        Assert.Contains(interiorGap ? "expected message 2" : "expected message 1", failure.Message);
        Assert.Same(failure, repeated);
        Assert.Equal(calls, storage.ReadCallCount);
        Assert.DoesNotContain(nameof(DbStoredQueries.CleanupStreamMessagesKey), storage.Parameters.Keys);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task Read_CommittedCleanupWithLostResponsePermanentlyFaultsSource(int deletedCount, bool appendAfterCleanup)
    {
        var storage = new CapturingRelationalStorage { ReadRecords = [MessageRecord(1), MessageRecord(2), MessageRecord(3)] };
        var lostResponse = new IOException("Cleanup committed before the connection failed.");
        storage.OnCleanup = () =>
        {
            storage.OnCleanup = null;
            storage.ReadRecords = deletedCount == 1
                ? [MessageRecord(2), MessageRecord(3)]
                : appendAfterCleanup ? [MessageRecord(4)] : [];
            storage.PartitionRecord = PartitionRecord(
                checkpoint: 0,
                nextMessageId: appendAfterCleanup ? 5 : 4,
                earliest: deletedCount == 1 ? 2 : appendAfterCleanup ? 4 : null,
                tail: appendAfterCleanup ? 4 : deletedCount == 1 ? 3 : null);
            throw lostResponse;
        };
        var source = CreateSource(storage);

        Assert.Same(lostResponse, await Assert.ThrowsAsync<IOException>(
            () => source.Read(3, TestContext.Current.CancellationToken)));
        var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(3, TestContext.Current.CancellationToken));
        var calls = storage.ReadCallCount;
        Assert.Same(failure, await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(3, TestContext.Current.CancellationToken)));

        Assert.Contains("after admitted message 0", failure.Message);
        Assert.Equal(calls, storage.ReadCallCount);
        Assert.Contains(nameof(DbStoredQueries.GetStreamPartitionBoundsKey), storage.Parameters.Keys);
        Assert.DoesNotContain(nameof(DbStoredQueries.AcquireStreamPartitionKey), storage.Parameters.Keys);
    }

    [Fact]
    public async Task Read_FailedCleanupWithIntactHistoryRetriesFromAdmittedOffset()
    {
        var storage = new CapturingRelationalStorage
        {
            ReadRecords = [MessageRecord(1), MessageRecord(2)],
            PartitionRecord = PartitionRecord(0, 3, 1, 2),
        };
        storage.OnCleanup = () =>
        {
            storage.OnCleanup = null;
            throw new IOException("Cleanup rolled back.");
        };
        var source = CreateSource(storage);

        await Assert.ThrowsAsync<IOException>(() => source.Read(2, TestContext.Current.CancellationToken));
        var messages = await source.Read(2, TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L], messages.Select(message => message.MessageId));
        source.MessagesAdded(messages);
        storage.ReadRecords = [MessageRecord(3)];
        Assert.Equal(3, Assert.Single(await source.Read(1, TestContext.Current.CancellationToken)).MessageId);
        Assert.DoesNotContain(nameof(DbStoredQueries.AcquireStreamPartitionKey), storage.Parameters.Keys);
    }

    [Fact]
    public async Task Read_InitialRetentionBaselineResumesBeforeEarliestRetainedMessage()
    {
        var storage = new CapturingRelationalStorage
        {
            PartitionRecord = PartitionRecord(9, 12, 10, 11),
            ReadRecords = [MessageRecord(10), MessageRecord(11)],
        };
        var source = CreateSource(storage);
        var loaded = await source.Load(TestContext.Current.CancellationToken);
        await source.Initialize(new(loaded.Checkpoint, startFromNow: false), TestContext.Current.CancellationToken);

        Assert.Equal("9", loaded.Checkpoint);
        var messages = await source.Read(2, TestContext.Current.CancellationToken);
        Assert.Equal([10L, 11L], messages.Select(message => message.MessageId));
    }

    [Fact]
    public async Task Load_RetentionGapPermanentlyFaultsWithoutReacquiring()
    {
        var storage = new CapturingRelationalStorage { PartitionRecord = PartitionRecord(0, 4, null, null) };
        var source = CreateSource(storage);
        var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Load(TestContext.Current.CancellationToken).AsTask());
        var calls = storage.ReadCallCount;

        Assert.Same(failure, await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Load(TestContext.Current.CancellationToken).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(1, TestContext.Current.CancellationToken)));
        Assert.Equal(calls, storage.ReadCallCount);
    }

    [Fact]
    public async Task Read_CheckpointedHistoryHolesPreserveUnreadContinuity()
    {
        var storage = new CapturingRelationalStorage
        {
            PartitionRecord = PartitionRecord(3, 5, 1, 4),
            ReadRecords = [MessageRecord(4)],
        };
        var source = CreateSource(storage);
        await source.Load(TestContext.Current.CancellationToken);

        Assert.Equal(4, Assert.Single(await source.Read(1, TestContext.Current.CancellationToken)).MessageId);
    }

    [Theory]
    [InlineData(0, 4, null, null, true)]
    [InlineData(2, 4, 1, 2, true)]
    [InlineData(3, 4, null, null, false)]
    [InlineData(3, 5, 4, 4, false)]
    public async Task Read_EmptyBatchReconcilesDeletedHistoryAndConcurrentArrivals(
        long checkpoint, long nextMessageId, long? earliest, long? tail, bool gap)
    {
        var storage = new CapturingRelationalStorage();
        var source = CreateSource(storage);
        await source.Load(TestContext.Current.CancellationToken);
        await source.Initialize(new(checkpoint.ToString(System.Globalization.CultureInfo.InvariantCulture), false), TestContext.Current.CancellationToken);
        storage.PartitionRecord = PartitionRecord(checkpoint, nextMessageId, earliest, tail);

        if (gap)
        {
            var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
                () => source.Read(1, TestContext.Current.CancellationToken));
            Assert.Same(failure, await Assert.ThrowsAsync<DataNotAvailableException>(
                () => source.Read(1, TestContext.Current.CancellationToken)));
        }
        else
        {
            Assert.Empty(await source.Read(1, TestContext.Current.CancellationToken));
            storage.ReadRecords = [MessageRecord(checkpoint + 1)];
            Assert.Equal(checkpoint + 1, Assert.Single(await source.Read(1, TestContext.Current.CancellationToken)).MessageId);
        }
    }

    [Theory]
    [InlineData(null, 1L, null, false)]
    [InlineData(0L, 1L, null, false)]
    [InlineData(3L, 4L, null, false)]
    [InlineData(0L, 4L, null, true)]
    [InlineData(0L, 4L, 2L, true)]
    public void HasRetentionGap_UsesNextMessageIdWhenRetainedHistoryIsEmpty(
        long? checkpoint,
        long nextMessageId,
        long? earliestMessageId,
        bool expected)
    {
        var state = new AdoNetStreamPartitionState(
            "service",
            "provider",
            "queue",
            OwnerEpoch: 1,
            NextMessageId: nextMessageId,
            Checkpoint: checkpoint,
            EarliestMessageId: earliestMessageId,
            TailMessageId: null);

        Assert.Equal(expected, AdoNetRecoverableStream.HasRetentionGap(state));
    }

    private static RelationalOrleansQueries CreateQueries(IRelationalStorage storage)
    {
        var queryValues = typeof(DbStoredQueries)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(property => property.Name, property =>
                property.Name == nameof(DbStoredQueries.StreamSchemaVersionKey) ? "2" : property.Name);
        return new RelationalOrleansQueries(storage, new DbStoredQueries(queryValues));
    }

    private static IDataRecord Record(params (string Name, object? Value)[] values)
        => new DictionaryDataRecord(values.ToDictionary(value => value.Name, value => value.Value));

    private static AdoNetRecoverableStream CreateSource(IRelationalStorage storage)
        => new("service", "provider", "queue", new AdoNetStreamOptions { MaxMessagesPerRead = 10 }, CreateQueries(storage), NullLogger.Instance);

    private static IDataRecord MessageRecord(long messageId)
        => Record(
            (nameof(AdoNetStreamMessage.ServiceId), "service"),
            (nameof(AdoNetStreamMessage.ProviderId), "provider"),
            (nameof(AdoNetStreamMessage.QueueId), "queue"),
            (nameof(AdoNetStreamMessage.MessageId), messageId),
            (nameof(AdoNetStreamMessage.StreamIdBytes), new byte[] { 1 }),
            (nameof(AdoNetStreamMessage.StreamNamespaceLength), 0),
            (nameof(AdoNetStreamMessage.CreatedOn), DateTime.UtcNow),
            (nameof(AdoNetStreamMessage.Payload), new byte[] { 2 }));

    private static IDataRecord PartitionRecord(long checkpoint, long nextMessageId, long? earliest, long? tail)
        => Record(
            (nameof(AdoNetStreamPartitionState.ServiceId), "service"),
            (nameof(AdoNetStreamPartitionState.ProviderId), "provider"),
            (nameof(AdoNetStreamPartitionState.QueueId), "queue"),
            (nameof(AdoNetStreamPartitionState.OwnerEpoch), 1L),
            (nameof(AdoNetStreamPartitionState.NextMessageId), nextMessageId),
            (nameof(AdoNetStreamPartitionState.Checkpoint), checkpoint),
            (nameof(AdoNetStreamPartitionState.EarliestMessageId), earliest),
            (nameof(AdoNetStreamPartitionState.TailMessageId), tail));

    private sealed class BlockingAdoNetQueueAdapterFactory(
        RelationalOrleansQueries queries,
        IQueueAdapter adapter,
        FakeHostApplicationLifetime lifetime,
        TimeSpan? initializationTimeout = null)
        : AdoNetQueueAdapterFactory(
            "provider",
            new AdoNetStreamOptions { InitializationTimeout = initializationTimeout ?? TimeSpan.FromSeconds(5) },
            new ClusterOptions(),
            new SimpleQueueCacheOptions(),
            new HashRingStreamQueueMapperOptions(),
            NullLoggerFactory.Instance,
            lifetime,
            Substitute.For<IServiceProvider>())
    {
        private readonly TaskCompletionSource _adapterConstruction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _adapterConstructionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _adapterConstructionCount;

        public int AdapterConstructionCount => Volatile.Read(ref _adapterConstructionCount);

        public TaskCompletionSource AdapterConstructionStarted => _adapterConstructionStarted;

        public void CompleteAdapterConstruction() => _adapterConstruction.SetResult();

        internal override ValueTask<RelationalOrleansQueries> GetQueriesAsync() => new(queries);

        internal override async ValueTask<IQueueAdapter> CreateAdapterCore(RelationalOrleansQueries value)
        {
            Assert.Same(queries, value);
            Interlocked.Increment(ref _adapterConstructionCount);
            _adapterConstructionStarted.TrySetResult();
            await _adapterConstruction.Task;
            return adapter;
        }
    }

    private sealed class BlockingRelationalStorage : IRelationalStorage
    {
        private readonly TaskCompletionSource<AdoNetStreamPartitionState> acquisition =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AcquisitionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CapturedCancellationToken { get; private set; }

        public string InvariantName => AdoNetInvariants.InvariantNameSqlServer;

        public string ConnectionString => string.Empty;

        public void CompleteAcquisition(long ownerEpoch)
            => acquisition.SetResult(new(
                "service",
                "provider",
                "queue",
                ownerEpoch,
                NextMessageId: 1,
                Checkpoint: 0,
                EarliestMessageId: null,
                TailMessageId: null));

        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            CapturedCancellationToken = cancellationToken;
            AcquisitionStarted.TrySetResult();
            var state = await acquisition.Task;
            using var command = new SqlCommand();
            parameterProvider?.Invoke(command);
            var record = new DictionaryDataRecord(new Dictionary<string, object?>
            {
                [nameof(AdoNetStreamPartitionState.ServiceId)] = state.ServiceId,
                [nameof(AdoNetStreamPartitionState.ProviderId)] = state.ProviderId,
                [nameof(AdoNetStreamPartitionState.QueueId)] = state.QueueId,
                [nameof(AdoNetStreamPartitionState.OwnerEpoch)] = state.OwnerEpoch,
                [nameof(AdoNetStreamPartitionState.NextMessageId)] = state.NextMessageId,
                [nameof(AdoNetStreamPartitionState.Checkpoint)] = state.Checkpoint,
                [nameof(AdoNetStreamPartitionState.EarliestMessageId)] = state.EarliestMessageId,
                [nameof(AdoNetStreamPartitionState.TailMessageId)] = state.TailMessageId,
            });
            return [await selector(record, 0, cancellationToken)];
        }

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DictionaryDataRecord(IReadOnlyDictionary<string, object?> values) : IDataRecord
    {
        private readonly string[] names = values.Keys.ToArray();

        public object this[int i] => GetValue(i);
        public object this[string name] => values[name] ?? DBNull.Value;
        public int FieldCount => names.Length;
        public bool GetBoolean(int i) => (bool)GetValue(i);
        public byte GetByte(int i) => (byte)GetValue(i);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => (char)GetValue(i);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => GetFieldType(i).Name;
        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
        public decimal GetDecimal(int i) => (decimal)GetValue(i);
        public double GetDouble(int i) => (double)GetValue(i);
        public Type GetFieldType(int i) => GetValue(i).GetType();
        public float GetFloat(int i) => (float)GetValue(i);
        public Guid GetGuid(int i) => (Guid)GetValue(i);
        public short GetInt16(int i) => (short)GetValue(i);
        public int GetInt32(int i) => (int)GetValue(i);
        public long GetInt64(int i) => (long)GetValue(i);
        public string GetName(int i) => names[i];
        public int GetOrdinal(string name) => Array.IndexOf(names, name);
        public string GetString(int i) => (string)GetValue(i);
        public object GetValue(int i) => values[names[i]] ?? DBNull.Value;
        public int GetValues(object[] destination)
        {
            var count = Math.Min(destination.Length, FieldCount);
            for (var i = 0; i < count; i++)
            {
                destination[i] = GetValue(i);
            }

            return count;
        }
        public bool IsDBNull(int i) => values[names[i]] is null or DBNull;
    }

    private sealed class CapturingRelationalStorage : IRelationalStorage
    {
        public CancellationToken CapturedCancellationToken { get; private set; }

        public Dictionary<string, Dictionary<string, object?>> Parameters { get; } = [];

        public IReadOnlyList<IDataRecord> ReadRecords { get; set; } = [];

        public IDataRecord PartitionRecord { get; set; } = AdoNetRecoverableStreamTests.PartitionRecord(0, 1, null, null);

        public Action? OnCleanup { get; set; }

        public IReadOnlyList<IDataRecord>? CleanupRecords { get; set; }

        public int ReadCallCount { get; private set; }

        public string InvariantName => AdoNetInvariants.InvariantNameSqlServer;

        public string ConnectionString => string.Empty;

        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            CapturedCancellationToken = cancellationToken;
            using var command = new SqlCommand();
            parameterProvider?.Invoke(command);
            Parameters[query] = command.Parameters.Cast<SqlParameter>()
                .ToDictionary(parameter => parameter.ParameterName, parameter =>
                    parameter.Value is DBNull ? null : parameter.Value);
            if (query == nameof(DbStoredQueries.CleanupStreamMessagesKey))
            {
                OnCleanup?.Invoke();
            }

            var records = query switch
            {
                nameof(DbStoredQueries.AcquireStreamPartitionKey) or nameof(DbStoredQueries.GetStreamPartitionBoundsKey) => [PartitionRecord],
                nameof(DbStoredQueries.ReadStreamMessagesKey) => ReadRecords,
                nameof(DbStoredQueries.AdvanceStreamCheckpointKey) =>
                [
                    Record(
                        (nameof(AdoNetStreamCheckpointUpdate.ServiceId), "service"),
                        (nameof(AdoNetStreamCheckpointUpdate.ProviderId), "provider"),
                        (nameof(AdoNetStreamCheckpointUpdate.QueueId), "queue"),
                        (nameof(AdoNetStreamCheckpointUpdate.OwnerEpoch), 1L),
                        (nameof(AdoNetStreamCheckpointUpdate.Checkpoint), 1L),
                        (nameof(AdoNetStreamCheckpointUpdate.Updated), true)),
                ],
                nameof(DbStoredQueries.CleanupStreamMessagesKey) when CleanupRecords is not null => CleanupRecords,
                nameof(DbStoredQueries.CleanupStreamMessagesKey) =>
                [
                    Record(
                        (nameof(AdoNetStreamCleanupResult.Ran), true),
                        (nameof(AdoNetStreamCleanupResult.DeletedCount), 0),
                        (nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId), null),
                        (nameof(AdoNetStreamCleanupResult.HardDeletedCount), 0),
                        (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), null),
                        (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), null),
                        (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                        (nameof(AdoNetStreamCleanupResult.EarliestMessageId), null),
                        (nameof(AdoNetStreamCleanupResult.TailMessageId), null)),
                ],
                _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
            };
            var results = new List<TResult>();
            foreach (var record in records)
            {
                results.Add(await selector(record, 0, cancellationToken));
            }

            return results;
        }

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

    }

    private sealed class ReservedReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public Task Initialize(TimeSpan timeout) => Task.CompletedTask;
        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) => Task.FromResult<IList<IBatchContainer>>([]);
        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) => Task.CompletedTask;
        public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;
        public int GetMaxAddCount() => 1;
        public void AddToCache(IList<IBatchContainer> messages) { }
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null!;
            return false;
        }
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token) => throw new NotSupportedException();
        public bool IsUnderPressure() => false;
    }

    public enum AcquisitionCompletionKind
    {
        Success,
        Fault,
        Canceled,
    }

    public enum StreamQueryKind
    {
        Read,
        Advance,
        Cleanup,
        Bounds,
    }
}
