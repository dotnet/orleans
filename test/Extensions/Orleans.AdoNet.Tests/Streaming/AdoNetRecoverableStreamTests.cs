using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
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
                    "service", "provider", "queue", 1, 1, 2, 3, 4, cancellation.Token);
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

        _ = await source.Load(TestContext.Current.CancellationToken);
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
                    (nameof(AdoNetStreamMessage.MessageId), 10L),
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
                    (nameof(AdoNetStreamCleanupResult.ActiveReplayWatermark), null),
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

        _ = await source.Load(TestContext.Current.CancellationToken);
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
                    (nameof(AdoNetStreamMessage.MessageId), 10L),
                    (nameof(AdoNetStreamMessage.StreamIdBytes), new byte[] { 1 }),
                    (nameof(AdoNetStreamMessage.StreamNamespaceLength), 0),
                    (nameof(AdoNetStreamMessage.CreatedOn), DateTime.UtcNow),
                    (nameof(AdoNetStreamMessage.Payload), new byte[] { 2 })),
            ],
            CleanupRecords =
            [
                Record(
                    (nameof(AdoNetStreamCleanupResult.Ran), true),
                    (nameof(AdoNetStreamCleanupResult.DeletedCount), 10),
                    (nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId), 10L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedCount), 10),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), 1L),
                    (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), 10L),
                    (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                    (nameof(AdoNetStreamCleanupResult.ActiveReplayWatermark), null),
                    (nameof(AdoNetStreamCleanupResult.EarliestMessageId), 11L),
                    (nameof(AdoNetStreamCleanupResult.TailMessageId), 11L)),
            ],
        };
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions { MaxMessagesPerRead = 10 },
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);
        var messages = await source.Read(10, TestContext.Current.CancellationToken);

        source.MessagesAddFailed(messages);
        var readsAfterFailure = storage.ReadCallCount;
        var failure = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));

        Assert.Contains("cache admission failed", failure.Message);
        Assert.Equal(readsAfterFailure, storage.ReadCallCount);
    }

    [Fact]
    public async Task ReplaySource_RenewsSafeWatermarkAndReleasesOnCursorDisposal()
    {
        var storage = new CapturingRelationalStorage();
        storage.ReplayMessages.Add(new(
            "service",
            "provider",
            "queue",
            1,
            [1],
            0,
            DateTime.UtcNow,
            []));
        var timeProvider = new FakeTimeProvider();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions
            {
                ReplayLeaseDuration = TimeSpan.FromSeconds(3),
                ReplayLeaseRenewalInterval = TimeSpan.FromSeconds(1),
            },
            CreateQueries(storage),
            NullLogger.Instance,
            timeProvider);
        _ = await source.Load(TestContext.Current.CancellationToken);
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        var replay = await ((IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source).Create(
            streamId,
            new AdoNetStreamSequenceToken("service", "provider", "queue", 1),
            TestContext.Current.CancellationToken);

        var page = await replay.Read(10, TestContext.Current.CancellationToken);
        replay.MessagesAdded(page.Messages);
        replay.UpdateProgress(new AdoNetStreamSequenceToken("service", "provider", "queue", 1));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await storage.ReplayLeaseUpdated.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await replay.DisposeAsync();

        Assert.True(page.IsAtTail);
        Assert.Equal(1, Assert.Single(page.Messages).MessageId);
        Assert.Equal(
            1L,
            Assert.IsType<long>(
                storage.Parameters[nameof(DbStoredQueries.UpdateStreamReplayLeaseKey)][nameof(DbStoredQueries.Columns.Watermark)]));
        Assert.Equal(1, storage.CallCounts[nameof(DbStoredQueries.ReleaseStreamReplayLeaseKey)]);
    }

    [Fact]
    public async Task ReplaySource_ReceiverShutdownPreservesLeaseForOwnershipTransfer()
    {
        var storage = new CapturingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);
        var replay = await ((IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source).Create(
            StreamId.Create("namespace", Guid.NewGuid()),
            new AdoNetStreamSequenceToken("service", "provider", "queue", 1),
            TestContext.Current.CancellationToken);

        await replay.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.False(storage.CallCounts.ContainsKey(nameof(DbStoredQueries.ReleaseStreamReplayLeaseKey)));
    }

    [Theory]
    [InlineData(AdoNetStreamReplayStatus.HistoryUnavailable, typeof(DataNotAvailableException))]
    [InlineData(AdoNetStreamReplayStatus.Expired, typeof(DataNotAvailableException))]
    [InlineData(AdoNetStreamReplayStatus.OwnershipLost, typeof(InvalidOperationException))]
    public async Task ReplaySource_AdmissionFailuresSurfacePreciseErrors(
        string status,
        Type exceptionType)
    {
        var storage = new CapturingRelationalStorage
        {
            ReplayAcquireStatus = status,
        };
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync(
            exceptionType,
            async () => await ((IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source).Create(
                StreamId.Create("namespace", Guid.NewGuid()),
                new AdoNetStreamSequenceToken("service", "provider", "queue", 1),
                TestContext.Current.CancellationToken));

        Assert.Contains("replay", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receiver_ReplaysOlderAdoNetHistoryAndHandsOffWithoutGap()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<AdoNetBatchContainer>>();
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        AdoNetStreamMessage CreateMessage(long messageId)
            => new(
                "service",
                "provider",
                "queue",
                messageId,
                streamId.FullKey.ToArray(),
                streamId.Namespace.Length,
                DateTime.UtcNow,
                AdoNetBatchContainer.ToMessagePayload(
                    serializer,
                    streamId,
                    [messageId],
                    requestContext: null));

        var storage = new CapturingRelationalStorage();
        storage.LiveMessages.AddRange([CreateMessage(4), CreateMessage(5)]);
        storage.ReplayMessages.AddRange(
            [CreateMessage(1), CreateMessage(2), CreateMessage(3), CreateMessage(4), CreateMessage(5)]);
        var receiver = new AdoNetQueueAdapterReceiver(
            "provider",
            "queue",
            new AdoNetStreamOptions
            {
                ReplayLeaseDuration = TimeSpan.FromSeconds(30),
                ReplayLeaseRenewalInterval = TimeSpan.FromSeconds(10),
            },
            new ClusterOptions { ServiceId = "service" },
            new SimpleQueueCacheOptions { CacheSize = 10 },
            CreateQueries(storage),
            serializer,
            NullLogger<AdoNetQueueAdapterReceiver>.Instance,
            new RecoverableStreamReplayOptions
            {
                MaxConcurrentReaders = 2,
                MaxPendingReaders = 2,
                CacheSize = 10,
                ReadBatchSize = 10,
                TemporaryTailRetryDelay = TimeSpan.Zero,
            });
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        _ = await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);
        using var cursor = Assert.IsAssignableFrom<IAsyncQueueCacheCursor>(
            receiver.GetCacheCursor(
                streamId,
                new AdoNetStreamSequenceToken("service", "provider", "queue", 2)));
        var delivered = new List<long>();
        while (true)
        {
            var moveResult = await cursor.MoveNextAsync(TestContext.Current.CancellationToken);
            if (moveResult == QueueCacheCursorMoveNextResult.Completed)
            {
                if (cursor.MoveNext())
                {
                    moveResult = QueueCacheCursorMoveNextResult.ItemAvailable;
                }
                else
                {
                    break;
                }
            }

            if (moveResult != QueueCacheCursorMoveNextResult.ItemAvailable)
            {
                break;
            }

            var batch = Assert.IsType<AdoNetBatchContainer>(cursor.GetCurrent(out var exception));
            Assert.Null(exception);
            delivered.Add(batch.SequenceToken.SequenceNumber);
        }

        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal([2, 3, 4, 5], delivered);
        Assert.Equal(1, storage.CallCounts[nameof(DbStoredQueries.AcquireStreamReplayLeaseKey)]);
        Assert.Equal(1, storage.CallCounts[nameof(DbStoredQueries.ReleaseStreamReplayLeaseKey)]);
    }

    [Fact]
    public async Task ReplaySource_RejectsForeignAdoNetTokensBeforeLeaseAdmission()
    {
        var storage = new CapturingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);
        var factory = (IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source;
        var streamId = StreamId.Create("namespace", Guid.NewGuid());

        await Assert.ThrowsAsync<DataNotAvailableException>(
            async () => await factory.Create(
                streamId,
                new EventSequenceToken(1),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DataNotAvailableException>(
            async () => await factory.Create(
                streamId,
                new AdoNetStreamSequenceToken("other-service", "provider", "queue", 1),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DataNotAvailableException>(
            async () => await factory.Create(
                streamId,
                new AdoNetStreamSequenceToken("service", "other-provider", "queue", 1),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DataNotAvailableException>(
            async () => await factory.Create(
                streamId,
                new AdoNetStreamSequenceToken("service", "provider", "other-queue", 1),
                TestContext.Current.CancellationToken));

        Assert.False(storage.CallCounts.ContainsKey(nameof(DbStoredQueries.AcquireStreamReplayLeaseKey)));
    }

    [Fact]
    public async Task ReplaySource_AcceptsLegacyEventSequenceTokenV2()
    {
        var storage = new CapturingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);

        await using var replay = await ((IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source).Create(
            StreamId.Create("namespace", Guid.NewGuid()),
            new EventSequenceTokenV2(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, storage.CallCounts[nameof(DbStoredQueries.AcquireStreamReplayLeaseKey)]);
    }

    [Fact]
    public async Task ReplaySource_RejectsNegativeSequenceTokensBeforeLeaseAdmission()
    {
        var storage = new CapturingRelationalStorage();
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);
        var factory = (IRecoverableStreamReplaySourceFactory<AdoNetStreamMessage>)source;
        var streamId = StreamId.Create("namespace", Guid.NewGuid());
        StreamSequenceToken[] tokens =
        [
            new EventSequenceTokenV2(-1),
            new AdoNetStreamSequenceToken("service", "provider", "queue", -1),
        ];

        foreach (var token in tokens)
        {
            await Assert.ThrowsAsync<DataNotAvailableException>(
                async () => await factory.Create(
                    streamId,
                    token,
                    TestContext.Current.CancellationToken));
        }

        Assert.False(storage.CallCounts.ContainsKey(nameof(DbStoredQueries.AcquireStreamReplayLeaseKey)));
    }

    [Fact]
    public async Task LiveReader_HardRetentionBeyondMaterializedPageSurfacesDataNotAvailable()
    {
        var storage = new CapturingRelationalStorage
        {
            CleanupHardDeletedCount = 2,
            CleanupHardDeletedThroughMessageId = 2,
        };
        var source = new AdoNetRecoverableStream(
            "service",
            "provider",
            "queue",
            new AdoNetStreamOptions(),
            CreateQueries(storage),
            NullLogger.Instance);
        _ = await source.Load(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DataNotAvailableException>(
            () => source.Read(10, TestContext.Current.CancellationToken));

        Assert.Contains("lost unread retained records after message 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("through message 2", exception.Message, StringComparison.Ordinal);
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
                property.Name == nameof(DbStoredQueries.StreamSchemaVersionKey) ? "3" : property.Name);
        return new RelationalOrleansQueries(storage, new DbStoredQueries(queryValues));
    }

    private static IDataRecord Record(params (string Name, object? Value)[] values)
        => new DictionaryDataRecord(values.ToDictionary(value => value.Name, value => value.Value));

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
        public Dictionary<string, int> CallCounts { get; } = [];
        public TaskCompletionSource ReplayLeaseUpdated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ReplayAcquireStatus { get; init; } = AdoNetStreamReplayStatus.Acquired;
        public List<AdoNetStreamMessage> LiveMessages { get; } = [];
        public List<AdoNetStreamMessage> ReplayMessages { get; } = [];
        public int CleanupHardDeletedCount { get; init; }
        public long? CleanupHardDeletedThroughMessageId { get; init; }

        public IReadOnlyList<IDataRecord> ReadRecords { get; init; } = [];

        public IReadOnlyList<IDataRecord>? CleanupRecords { get; init; }

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
            CallCounts[query] = CallCounts.GetValueOrDefault(query) + 1;
            var records = query switch
            {
                nameof(DbStoredQueries.AcquireStreamPartitionKey) => [PartitionRecord()],
                nameof(DbStoredQueries.ReadStreamMessagesKey) => ReadRecords.Count > 0
                    ? ReadRecords
                    : LiveMessages.Select(MessageRecord).ToArray(),
                nameof(DbStoredQueries.AcquireStreamReplayLeaseKey) =>
                [
                    ReplayLeaseRecord(
                        ReplayAcquireStatus,
                        includeIdentity: true,
                        watermark: 0,
                        tailMessageId: 1),
                ],
                nameof(DbStoredQueries.ReadStreamReplayMessagesKey) => ReplayReadRecords(Parameters[query]),
                nameof(DbStoredQueries.UpdateStreamReplayLeaseKey) =>
                [
                    ReplayLeaseRecord(
                        AdoNetStreamReplayStatus.Active,
                        includeIdentity: false,
                        watermark: Convert.ToInt64(Parameters[query][nameof(DbStoredQueries.Columns.Watermark)]),
                        tailMessageId: 1),
                ],
                nameof(DbStoredQueries.ReleaseStreamReplayLeaseKey) =>
                [
                    ReplayLeaseRecord(
                        AdoNetStreamReplayStatus.Released,
                        includeIdentity: false,
                        watermark: 1,
                        tailMessageId: 1),
                ],
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
                        (nameof(AdoNetStreamCleanupResult.HardDeletedCount), CleanupHardDeletedCount),
                        (nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId), null),
                        (nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId), CleanupHardDeletedThroughMessageId),
                        (nameof(AdoNetStreamCleanupResult.Checkpoint), 0L),
                        (nameof(AdoNetStreamCleanupResult.ActiveReplayWatermark), null),
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

            if (query == nameof(DbStoredQueries.UpdateStreamReplayLeaseKey))
            {
                ReplayLeaseUpdated.TrySetResult();
            }

            return results;
        }

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private IDataRecord PartitionRecord()
        {
            var messages = LiveMessages.Concat(ReplayMessages).ToArray();
            return Record(
                (nameof(AdoNetStreamPartitionState.ServiceId), "service"),
                (nameof(AdoNetStreamPartitionState.ProviderId), "provider"),
                (nameof(AdoNetStreamPartitionState.QueueId), "queue"),
                (nameof(AdoNetStreamPartitionState.OwnerEpoch), 1L),
                (nameof(AdoNetStreamPartitionState.NextMessageId), messages.Length == 0 ? 1L : messages.Max(static message => message.MessageId) + 1),
                (nameof(AdoNetStreamPartitionState.Checkpoint), 0L),
                (nameof(AdoNetStreamPartitionState.EarliestMessageId), messages.Length == 0 ? null : messages.Min(static message => message.MessageId)),
                (nameof(AdoNetStreamPartitionState.TailMessageId), messages.Length == 0 ? null : messages.Max(static message => message.MessageId)));
        }

        private IDataRecord[] ReplayReadRecords(IReadOnlyDictionary<string, object?> parameters)
        {
            var afterMessageId = Convert.ToInt64(parameters[nameof(DbStoredQueries.Columns.AfterMessageId)]);
            var maxCount = Convert.ToInt32(parameters[nameof(DbStoredQueries.Columns.MaxCount)]);
            var tailMessageId = ReplayMessages.Count == 0 ? 0 : ReplayMessages.Max(static message => message.MessageId);
            var messages = ReplayMessages
                .Where(message => message.MessageId > afterMessageId)
                .Take(maxCount)
                .ToArray();
            return messages.Length == 0
                ?
                [
                    ReplayLeaseRecord(
                        AdoNetStreamReplayStatus.Active,
                        includeIdentity: false,
                        watermark: afterMessageId,
                        tailMessageId),
                ]
                : messages.Select(message => ReplayLeaseRecord(
                    AdoNetStreamReplayStatus.Active,
                    includeIdentity: false,
                    watermark: afterMessageId,
                    tailMessageId,
                    message)).ToArray();
        }

        private static IDataRecord MessageRecord(AdoNetStreamMessage message)
            => Record(
                (nameof(AdoNetStreamMessage.ServiceId), message.ServiceId),
                (nameof(AdoNetStreamMessage.ProviderId), message.ProviderId),
                (nameof(AdoNetStreamMessage.QueueId), message.QueueId),
                (nameof(AdoNetStreamMessage.MessageId), message.MessageId),
                (nameof(AdoNetStreamMessage.StreamIdBytes), message.StreamIdBytes),
                (nameof(AdoNetStreamMessage.StreamNamespaceLength), message.StreamNamespaceLength),
                (nameof(AdoNetStreamMessage.CreatedOn), message.CreatedOn),
                (nameof(AdoNetStreamMessage.Payload), message.Payload));

        private static IDataRecord ReplayLeaseRecord(
            string status,
            bool includeIdentity,
            long watermark,
            long tailMessageId,
            AdoNetStreamMessage? message = null)
        {
            var values = new List<(string Name, object? Value)>
            {
                (nameof(AdoNetStreamReplayLeaseState.Status), status),
                (nameof(AdoNetStreamReplayLeaseState.OwnerEpoch), 1L),
                (nameof(AdoNetStreamReplayLeaseState.Watermark), watermark),
                (nameof(AdoNetStreamReplayLeaseState.ExpiresOn), DateTime.UtcNow.AddMinutes(1)),
                (nameof(AdoNetStreamReplayLeaseState.NextMessageId), tailMessageId + 1),
                (nameof(AdoNetStreamReplayLeaseState.Checkpoint), 0L),
                (nameof(AdoNetStreamReplayLeaseState.EarliestMessageId), 1L),
                (nameof(AdoNetStreamReplayLeaseState.TailMessageId), tailMessageId),
            };
            if (includeIdentity)
            {
                values.Add((nameof(AdoNetStreamReplayLeaseState.ServiceId), "service"));
                values.Add((nameof(AdoNetStreamReplayLeaseState.ProviderId), "provider"));
                values.Add((nameof(AdoNetStreamReplayLeaseState.QueueId), "queue"));
                values.Add((nameof(AdoNetStreamReplayLeaseState.ReaderId), "reader"));
            }

            if (message is not null)
            {
                values.Add((nameof(AdoNetStreamMessage.MessageId), message.MessageId));
                values.Add((nameof(AdoNetStreamMessage.StreamIdBytes), message.StreamIdBytes));
                values.Add((nameof(AdoNetStreamMessage.StreamNamespaceLength), message.StreamNamespaceLength));
                values.Add((nameof(AdoNetStreamMessage.CreatedOn), message.CreatedOn));
                values.Add((nameof(AdoNetStreamMessage.Payload), message.Payload));
            }
            else if (!includeIdentity)
            {
                values.Add((nameof(AdoNetStreamMessage.MessageId), null));
                values.Add((nameof(AdoNetStreamMessage.StreamIdBytes), null));
                values.Add((nameof(AdoNetStreamMessage.StreamNamespaceLength), null));
                values.Add((nameof(AdoNetStreamMessage.CreatedOn), null));
                values.Add((nameof(AdoNetStreamMessage.Payload), null));
            }
            return Record([.. values]);
        }
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
    }
}
