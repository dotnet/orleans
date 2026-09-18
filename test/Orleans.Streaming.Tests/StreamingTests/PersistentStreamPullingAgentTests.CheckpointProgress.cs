using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using TestExtensions;
using Xunit;

#pragma warning disable CS0618 // Forward the legacy cache API used by the shared test fixtures.

namespace UnitTests.StreamingTests;

public partial class PersistentStreamPullingAgentTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void PersistentDeliveryRetriesAreOptIn()
        => Assert.False(new StreamPullingAgentOptions().RetryFailedDeliveries);

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CheckpointProgress_SameCursorHandshakePreservesUnsettledDelivery(bool pooled, bool retry, bool fail)
    {
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize: 2, retryFailedDeliveries: retry);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        var cursor = scenario.Idle.Cursor;
        var generation = scenario.Idle.HandshakeGeneration;
        var releaseDelivery = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<long>();
        var consumer = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                attempts.AddRange(CheckpointBatchSequences(batch));
                return attempts.Count == 2 ? releaseDelivery.Task : Task.FromResult<StreamHandshakeToken?>(null);
            },
        };
        scenario.Idle.StreamConsumer = consumer;
        var delivery = scenario.Accessor.RunConsumerCursor(scenario.Idle);
        try
        {
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await scenario.Accessor.AddSubscriber(scenario.Idle);
            Assert.Same(cursor, scenario.Idle.Cursor);
            Assert.Equal(generation + 1, scenario.Idle.HandshakeGeneration);
            if (fail)
                releaseDelivery.SetException(new InvalidOperationException("Delivery failed after same-cursor handshake"));
            else
                releaseDelivery.SetResult(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(200)));
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(new long[] { 3, 4 }, attempts);
            Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Empty(consumer.Errors);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

            await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0, 0), TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 3, 4, 3, 4 }, attempts);
            Assert.Same(cursor, scenario.Idle.Cursor);
            Assert.Equal(4, scenario.Idle.LastProcessedToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        finally
        {
            releaseDelivery.TrySetResult(null);
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_SameCursorHandshakeDuringErrorHandlingPreservesReplay(bool pooled)
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize: 2, deliveryBackoff: backoff);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        var errorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;
        var consumer = new RecordingConsumer
        {
            OnDelivery = _ => fail
                ? Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Delivery failed"))
                : Task.FromResult<StreamHandshakeToken?>(null),
            OnError = _ =>
            {
                errorStarted.TrySetResult();
                return releaseError.Task;
            },
        };
        scenario.Idle.StreamConsumer = consumer;
        var cursor = scenario.Idle.Cursor;
        var delivery = scenario.Accessor.RunConsumerCursor(scenario.Idle);
        try
        {
            await errorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await scenario.Accessor.AddSubscriber(scenario.Idle);
            Assert.Same(cursor, scenario.Idle.Cursor);
            fail = false;
            releaseError.SetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(3, Assert.Single(consumer.DeliveredTokens).SequenceNumber);
            Assert.IsType<StreamEventDeliveryFailureException>(Assert.Single(consumer.Errors));
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

            await scenario.Accessor.RunConsumerCursor(scenario.Idle);
            Assert.Equal(new long[] { 3, 3 }, consumer.DeliveredTokens.Select(token => token.SequenceNumber));
            Assert.Equal(4, scenario.Idle.LastProcessedToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        finally
        {
            releaseError.TrySetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_BusyPumpPublishesBeforeEachRead(bool pooled)
    {
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        await using var scenario = await CreateCheckpointScenario(pooled, receiver: receiver);
        var reads = 0;
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (reads > 0)
                    Assert.Equal(reads, scenario.Checkpoints[^1]?.SequenceNumber);
                reads++;
                return Task.FromResult<IList<IBatchContainer>>(reads <= 100
                    ? [new TestBatchContainer(scenario.Busy.StreamId.StreamId, new EventSequenceTokenV2(reads))]
                    : []);
            });

        await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0, 0), TestContext.Current.CancellationToken);

        Assert.Equal(101, reads);
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value),
            scenario.Checkpoints.Select(token => token!.SequenceNumber));
        Assert.Equal(100, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
        Assert.Equal(100, scenario.Busy.LastProcessedToken?.SequenceNumber);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_StickyMissNotifiesFailurePolicy(bool faultSubscription)
    {
        var handler = Substitute.For<IStreamFailureHandler>();
        handler.ShouldFaultSubsriptionOnError.Returns(faultSubscription);
        await using var scenario = await CreateCheckpointScenario(pooled: true, failureHandler: handler);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        var cursor = scenario.Idle.Cursor;
        var consumer = new RecordingConsumer();
        scenario.Idle.StreamConsumer = consumer;
        Assert.IsType<PurgeablePooledQueueCache>(scenario.Cache).Purge();

        await scenario.Accessor.RunConsumerCursor(scenario.Idle);

        Assert.Empty(consumer.DeliveredTokens);
        Assert.IsType<QueueCacheMissException>(consumer.Errors[0]);
        await handler.Received(1).OnDeliveryFailure(
            scenario.Idle.SubscriptionId, "provider", scenario.Idle.StreamId.StreamId, null);
        if (faultSubscription)
        {
            Assert.Equal(2, consumer.Errors.Count);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.Null(scenario.Idle.Cursor);
            Assert.False((await scenario.Accessor.GetPubSubCache()).ContainsKey(scenario.Idle.StreamId));
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        else
        {
            Assert.Single(consumer.Errors);
            Assert.Same(cursor, scenario.Idle.Cursor);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);
            await scenario.Accessor.RunConsumerCursor(scenario.Idle);
            Assert.Equal(2, consumer.Errors.Count);
            Assert.All(consumer.Errors, error => Assert.IsType<QueueCacheMissException>(error));
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_RemovingFailedInitialSubscriberReleasesRegistrationPin(bool keepHealthySubscriber)
    {
        var cache = new RecordingSimpleQueueCache();
        var streamId = new QualifiedStreamId("provider", StreamId.Create("registration", Guid.NewGuid()));
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Handshake retry budget exhausted"));
        var (accessor, pubSub, stream) = await CreateInitializedAgentWithStream(
            streamId, new EventSequenceTokenV2(1), cache, new StreamPullingAgentOptions(), deliveryBackoff: backoff);
        try
        {
            var consumer = new RecordingConsumer
            {
                OnHandshake = () => Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Initial handshake failed")),
            };
            var failed = stream.AddConsumer(GuidId.GetNewGuidId(), streamId, consumer, null, DateTime.UtcNow);
            var healthy = keepHealthySubscriber ? AddCheckpointConsumer(stream, streamId, cache) : null;
            cache.AddToCache(
            [
                new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(1)),
                new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(2)),
            ]);
            pubSub.RegisterProducer(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ISet<PubSubSubscriptionState>>(
                    new HashSet<PubSubSubscriptionState> { new(failed.SubscriptionId, streamId, default) }));
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            Assert.True(stream.StreamRegistered);
            Assert.Null(stream.RegistrationTask);
            Assert.NotNull(stream.RegistrationCursor);
            Assert.False(failed.IsRegistered);
            Assert.True(failed.HasUnresolvedHandshake);
            Assert.Single(consumer.Errors);
            if (healthy is not null) await accessor.RunConsumerCursor(healthy);
            Assert.False(cache.TryPurgeFromCache(out _));

            var agent = (PersistentStreamPullingAgent)accessor;
            await agent.RunOrQueueTask(() => agent.RemoveSubscriber(
                failed.SubscriptionId, streamId, TestContext.Current.CancellationToken));

            Assert.Null(stream.RegistrationCursor);
            Assert.Equal(keepHealthySubscriber, (await accessor.GetPubSubCache()).ContainsKey(streamId));
            Assert.True(cache.TryPurgeFromCache(out var purged));
            Assert.Equal(new long[] { 1, 2 }, purged.Select(batch => batch.SequenceToken.SequenceNumber));
        }
        finally
        {
            await accessor.Shutdown();
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptProvider_SkipsCertifiedCursorBookkeeping(bool pooled)
    {
        await using var scenario = await CreateCheckpointScenario(pooled, checkpointing: false);
        var cursor = new ObservedQueueCursor(scenario.Idle.Cursor!);
        scenario.Idle.Cursor = cursor;
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));

        Assert.Equal(0, cursor.ProgressEnables);
        Assert.Equal(0, cursor.ProgressReads);
        Assert.Equal(0, cursor.ProgressCompletions);
        Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
        Assert.Null(scenario.Idle.LastSafePartitionToken);
        if (!pooled)
        {
            Assert.True(scenario.Cache.TryPurgeFromCache(out var purged));
            Assert.Equal(new long[] { 1, 2 }, purged.Select(batch => batch.SequenceToken.SequenceNumber));
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_IdlePumpLeavesAccountedCursorsIdle(bool pooled)
    {
        await using var scenario = await CreateCheckpointScenario(pooled);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 200));
        var idle = new ObservedQueueCursor(scenario.Idle.Cursor!);
        var busy = new ObservedQueueCursor(scenario.Busy.Cursor!);
        scenario.Idle.Cursor = idle;
        scenario.Busy.Cursor = busy;
        var queue = QueueId.GetQueueId("queue", 0, 0);

        await scenario.Accessor.RunQueuePump(queue, TestContext.Current.CancellationToken);
        await scenario.Accessor.RunQueuePump(queue, TestContext.Current.CancellationToken);

        Assert.Equal(0, idle.Moves);
        Assert.Equal(0, busy.Moves);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);

        await scenario.Read((scenario.Busy, 201));
        Assert.Equal(1, idle.Moves);
        Assert.Equal(2, busy.Moves);
        Assert.Equal(201, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
        Assert.Equal(201, scenario.Busy.LastSafePartitionToken?.SequenceNumber);
        await scenario.Accessor.RunQueuePump(queue, TestContext.Current.CancellationToken);
        Assert.Equal(1, idle.Moves);
        Assert.Equal(2, busy.Moves);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 201);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointProgress_SnapshotsIsolateSynchronousSubscriptionChanges(bool pooled, bool usePump)
    {
        await using var scenario = await CreateCheckpointScenario(pooled);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        var stream = (await scenario.Accessor.GetPubSubCache())[scenario.Idle.StreamId];
        var second = AddCheckpointConsumer(stream, scenario.Idle.StreamId, scenario.Cache);
        var lateConsumer = new ImmediateRecordingConsumer();
        StreamConsumerData? late = null;
        var removed = false;
        void OnMove()
        {
            if (removed) return;
            removed = true;
            var agent = (PersistentStreamPullingAgent)scenario.Accessor;
            agent.RemoveSubscriber_Impl(second.SubscriptionId, second.StreamId);
            agent.RemoveSubscriber_Impl(scenario.Busy.SubscriptionId, scenario.Busy.StreamId);
            late = stream.AddConsumer(GuidId.GetGuidId(Guid.NewGuid()), scenario.Idle.StreamId, lateConsumer, null, DateTime.UtcNow);
            late.IsRegistered = true;
            late.Cursor = scenario.Cache.GetCacheCursor(late.StreamId, new EventSequenceTokenV2(3));
        }

        var cursor = new ObservedQueueCursor(scenario.Idle.Cursor!) { OnMove = OnMove };
        scenario.Idle.Cursor = cursor;
        if (usePump)
        {
            scenario.Idle.State = second.State = scenario.Busy.State = StreamConsumerDataState.Active;
        }
        await scenario.Read((scenario.Idle, 3));
        if (usePump)
        {
            scenario.Idle.State = second.State = scenario.Busy.State = StreamConsumerDataState.Inactive;
            await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0, 0), TestContext.Current.CancellationToken);
        }

        Assert.True(removed);
        Assert.NotNull(late);
        Assert.Equal(2, stream.Count);
        Assert.Null(second.Cursor);
        Assert.Null(scenario.Busy.Cursor);
        Assert.Single(await scenario.Accessor.GetPubSubCache());
        Assert.Empty(lateConsumer.DeliveredTokens);
        Assert.Equal(3, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);

        await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0, 0), TestContext.Current.CancellationToken);
        Assert.Equal(3, Assert.Single(lateConsumer.DeliveredTokens).SequenceNumber);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 3);
    }

    private sealed class ObservedQueueCursor(IQueueCacheCursor inner) : IQueueCacheCursor, IQueueCacheCursorProgress
    {
        public int Moves { get; private set; }
        public int ProgressEnables { get; private set; }
        public int ProgressReads { get; private set; }
        public int ProgressCompletions { get; private set; }
        public Action? OnMove { get; init; }
        public void Dispose() => inner.Dispose();
        public IBatchContainer? GetCurrent(out Exception? exception) => inner.GetCurrent(out exception);
        public bool MoveNext() => MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success;
        public QueueCacheCursorMoveResult MoveNextWithResult()
        {
            Moves++;
            OnMove?.Invoke();
            return inner.MoveNextWithResult();
        }
        public void Refresh(StreamSequenceToken token) => inner.Refresh(token);
        public void RecordDeliveryFailure() => inner.RecordDeliveryFailure();
        void IQueueCacheCursorProgress.RecordDeliveryFailure() => ((IQueueCacheCursorProgress)inner).RecordDeliveryFailure();
        public StreamSequenceToken? SafeSequenceToken
        {
            get
            {
                ProgressReads++;
                return ((IQueueCacheCursorProgress)inner).SafeSequenceToken;
            }
        }
        public void EnableDeliveryProgress()
        {
            ProgressEnables++;
            ((IQueueCacheCursorProgress)inner).EnableDeliveryProgress();
        }
        public void SetDeliveredThrough(StreamSequenceToken token) => ((IQueueCacheCursorProgress)inner).SetDeliveredThrough(token);
        public void RecordDeliveryCompletion()
        {
            ProgressCompletions++;
            ((IQueueCacheCursorProgress)inner).RecordDeliveryCompletion();
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptProvider_ExhaustedDeliveryPreservesItsSkipPolicy(bool retry)
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        await using var scenario = await CreateCheckpointScenario(deliveryBackoff: backoff, checkpointing: false, retryFailedDeliveries: retry);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        var acknowledged = new List<long>();
        var consumer = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                if (batch.SequenceToken.SequenceNumber == 3)
                {
                    return Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Delivery failed"));
                }
                acknowledged.Add(batch.SequenceToken.SequenceNumber);
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
        };
        scenario.Idle.StreamConsumer = consumer;
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        await scenario.Accessor.RunConsumerCursor(scenario.Idle);

        Assert.Equal(new long[] { 4 }, acknowledged);
        Assert.Equal(new long[] { 3, 4 }, consumer.DeliveredTokens.Select(token => token.SequenceNumber));
        Assert.IsType<StreamEventDeliveryFailureException>(Assert.Single(consumer.Errors));
        Assert.Equal(4, scenario.Idle.LastProcessedToken?.SequenceNumber);
        await scenario.Accessor.ReportDeliveryProgress();
        Assert.Empty(scenario.Checkpoints);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task CheckpointProgress_IdleStreamScansPartitionWithoutChangingItsAcknowledgedToken(bool pooled, int batchSize)
    {
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

        await scenario.Read((scenario.Busy, 100), (scenario.Busy, 200));

        Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
        Assert.Equal(200, scenario.Busy.LastProcessedToken?.SequenceNumber);
        Assert.Equal(200, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
        Assert.Equal(200, scenario.Busy.LastSafePartitionToken?.SequenceNumber);
        Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
        var idleConsumer = Assert.IsType<ImmediateRecordingConsumer>(scenario.Idle.StreamConsumer);
        Assert.Equal(1, Assert.Single(idleConsumer.DeliveredTokens).SequenceNumber);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task CheckpointProgress_CursorReadFailureRetriesFirstSelectionOnEmptyPump(bool pooled, int batchSize)
    {
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 100));
        scenario.Idle.State = StreamConsumerDataState.Inactive;

        var cursor = new ThrowingQueueCursor(scenario.Idle.Cursor!, successfulMoves: batchSize - 1);
        scenario.Idle.Cursor = cursor;
        var errorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<long>();
        var consumer = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                delivered.AddRange(CheckpointBatchSequences(batch));
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
            OnError = _ =>
            {
                errorStarted.TrySetResult();
                return releaseError.Task;
            },
        };
        scenario.Idle.StreamConsumer = consumer;

        var failedRun = scenario.Accessor.RunConsumerCursor(scenario.Idle);
        try
        {
            await errorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(failedRun.IsCompleted);
            Assert.Same(cursor.Failure, Assert.Single(consumer.Errors));
            Assert.Empty(delivered);
            Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(2, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(2, cursor.SafeSequenceToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

            releaseError.SetResult();
            await failedRun.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(cursor, scenario.Idle.Cursor);
            Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
            Assert.Empty(delivered);

            // The agent's receiver has no messages. Only the periodic retry can resume delivery.
            await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0u, 0u), TestContext.Current.CancellationToken);

            Assert.Same(cursor, scenario.Idle.Cursor);
            Assert.Equal(new long[] { 3, 4 }, delivered);
            Assert.Single(consumer.Errors);
            Assert.Equal(4, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(100, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(100, cursor.SafeSequenceToken?.SequenceNumber);
            Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
            Assert.Null(scenario.Idle.PendingBatch);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 100);
        }
        finally
        {
            releaseError.TrySetResult();
            await failedRun.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    public async Task CheckpointProgress_ExhaustedDeliveryFollowsConfiguredPolicy(bool pooled, int batchSize, bool retry)
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Injected exhausted delivery budget"));
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize, deliveryBackoff: backoff, retryFailedDeliveries: retry);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        var originalCursor = scenario.Idle.Cursor;
        var attempts = new List<long>();
        var acknowledged = new List<long>();
        var failDelivery = true;
        var errorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                var sequences = CheckpointBatchSequences(batch).ToArray();
                attempts.AddRange(sequences);
                if (failDelivery)
                {
                    return Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Injected delivery failure at three"));
                }

                acknowledged.AddRange(sequences);
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
            OnError = _ =>
            {
                errorStarted.TrySetResult();
                return releaseError.Task;
            },
        };
        scenario.Idle.StreamConsumer = consumer;
        var failedRun = scenario.Accessor.RunConsumerCursor(scenario.Idle);
        try
        {
            await errorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(batchSize == 1 ? new long[] { 3 } : new long[] { 3, 4 }, attempts);
            Assert.Empty(acknowledged);
            Assert.IsType<StreamEventDeliveryFailureException>(Assert.Single(consumer.Errors));
            Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(2, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

            failDelivery = false;
            releaseError.SetResult();
            await failedRun.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(originalCursor, scenario.Idle.Cursor);
            Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, retry ? 2 : 200);

            await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0u, 0u), TestContext.Current.CancellationToken);

            Assert.Equal(retry
                ? batchSize == 1 ? new long[] { 3, 3, 4 } : new long[] { 3, 4, 3, 4 }
                : new long[] { 3, 4 }, attempts);
            Assert.Equal(retry ? new long[] { 3, 4 } : batchSize == 1 ? new long[] { 4 } : [], acknowledged);
            backoff.Received(1).Next(Arg.Any<int>());
            Assert.Single(consumer.Errors);
            Assert.Same(originalCursor, scenario.Idle.Cursor);
            Assert.Equal(retry || batchSize == 1 ? 4 : 1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(200, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        finally
        {
            releaseError.TrySetResult();
            await failedRun.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointProgress_DefaultSkipPreservesRepositioningDuringErrorHandling(bool pooled, bool finishHandshakeFirst)
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize: 2, deliveryBackoff: backoff);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        var errorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandshake = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failDelivery = true;
        var delivered = new List<long>();
        var consumer = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                if (failDelivery) return Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Delivery failed"));
                delivered.AddRange(CheckpointBatchSequences(batch));
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
            OnError = _ =>
            {
                errorStarted.TrySetResult();
                return releaseError.Task;
            },
            OnHandshake = () =>
            {
                handshakeStarted.TrySetResult();
                return releaseHandshake.Task;
            },
        };
        scenario.Idle.StreamConsumer = consumer;
        var delivery = scenario.Accessor.RunConsumerCursor(scenario.Idle);
        Task handshake = Task.CompletedTask;
        try
        {
            await errorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            handshake = scenario.Accessor.AddSubscriber(scenario.Idle);
            await handshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (finishHandshakeFirst)
            {
                releaseHandshake.SetResult(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(3)));
                await handshake.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            releaseError.SetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(delivered);
            Assert.Equal(finishHandshakeFirst ? 3 : 1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            if (!finishHandshakeFirst)
            {
                await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);
                // Keep the newly attached cursor idle until the replay assertions below.
                scenario.Idle.State = StreamConsumerDataState.Active;
                releaseHandshake.SetResult(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(3)));
                await handshake.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                scenario.Idle.State = StreamConsumerDataState.Inactive;
            }

            Assert.Equal(3, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(3, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 3);
            failDelivery = false;
            await scenario.Accessor.RunConsumerCursor(scenario.Idle);
            Assert.Equal(new long[] { 4 }, delivered);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        finally
        {
            releaseError.TrySetResult();
            releaseHandshake.TrySetResult(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(3)));
            await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await handshake.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task CheckpointProgress_DefaultSkipRetainsDeliveryWhenErrorHandlingThrows()
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        var error = new InvalidOperationException("Synchronous failure handler exception");
        var failureHandler = Substitute.For<IStreamFailureHandler>();
        failureHandler.OnDeliveryFailure(Arg.Any<GuidId>(), Arg.Any<string>(), Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken>())
            .Returns(_ => throw error);
        await using var scenario = await CreateCheckpointScenario(deliveryBackoff: backoff, failureHandler: failureHandler);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        scenario.Idle.State = StreamConsumerDataState.Active;
        await scenario.Read((scenario.Idle, 3), (scenario.Idle, 4), (scenario.Busy, 200));
        scenario.Idle.State = StreamConsumerDataState.Inactive;
        scenario.Idle.StreamConsumer = new RecordingConsumer
        {
            OnDelivery = _ => Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Delivery failed")),
        };

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Accessor.RunConsumerCursor(scenario.Idle)));
        Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

        var recovered = new ImmediateRecordingConsumer();
        scenario.Idle.StreamConsumer = recovered;
        await scenario.Accessor.RunConsumerCursor(scenario.Idle);
        Assert.Equal(new long[] { 3, 4 }, recovered.DeliveredTokens.Select(token => token.SequenceNumber));
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_CertifiedEmptyReadRecoveryRestoresEstablishedPrefix(bool pooled)
    {
        await using var scenario = await CreateCheckpointScenario(pooled);
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);
        var queue = QueueId.GetQueueId("queue", 0u, 0u);
        var source = Substitute.For<IQueueAdapterReceiver>();
        var receiver = new CheckpointRecovery();
        var readFailure = new InvalidOperationException("Injected uncertain source read");
        source.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IList<IBatchContainer>>(readFailure), Task.FromResult<IList<IBatchContainer>>([]));
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnRecovery = _ =>
        {
            recoveryStarted.TrySetResult();
            return releaseRecovery.Task;
        };

        Assert.Same(readFailure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.Accessor.ReadFromQueue(queue, source, 1000)));
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);

        var unrelatedReceiver = Substitute.For<IQueueAdapterReceiver>();
        unrelatedReceiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<IBatchContainer>>([]));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => scenario.Accessor.ReadFromQueue(queue, unrelatedReceiver, 1000));
        await unrelatedReceiver.DidNotReceive().GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);

        ((ITestCheckpointingQueueCache)scenario.Cache).Recovery = receiver.RecoverReadAsync;
        var recoveringRead = scenario.Accessor.ReadFromQueue(queue, source, 1000);
        try
        {
            await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(recoveringRead.IsCompleted);
            Assert.Equal(CancellationToken.None, Assert.Single(receiver.RecoveryTokens));
            await source.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            Assert.Equal(2, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);

            releaseRecovery.SetResult();
            Assert.False(await recoveringRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(CancellationToken.None, Assert.Single(receiver.RecoveryTokens));
            await source.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);

            await scenario.Read((scenario.Busy, 200));
            Assert.Equal(1, scenario.Idle.LastProcessedToken?.SequenceNumber);
            Assert.Equal(200, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 200);
        }
        finally
        {
            releaseRecovery.TrySetResult();
            await recoveringRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_UnsuccessfulRecoveryCannotClearReadObligation(bool cancelRecovery)
    {
        await using var scenario = await CreateCheckpointScenario();
        await scenario.Read((scenario.Idle, 1), (scenario.Busy, 2));
        var queue = QueueId.GetQueueId("queue", 0u, 0u);
        var source = Substitute.For<IQueueAdapterReceiver>();
        var receiver = new CheckpointRecovery();
        var readFailure = new InvalidOperationException("Injected uncertain source read");
        source.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IList<IBatchContainer>>(readFailure), Task.FromResult<IList<IBatchContainer>>([]));
        Assert.Same(readFailure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => scenario.Accessor.ReadFromQueue(queue, source, 1000)));

        using var cancellation = new CancellationTokenSource();
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnRecovery = token =>
        {
            recoveryStarted.TrySetResult();
            return releaseRecovery.Task.WaitAsync(token);
        };
        ((ITestCheckpointingQueueCache)scenario.Cache).Recovery = receiver.RecoverReadAsync;
        var recoveringRead = scenario.Accessor.ReadFromQueueWithCancellation(queue, source, 1000, cancellation.Token);
        try
        {
            await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);
            if (cancelRecovery)
            {
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recoveringRead);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
            else
            {
                var recoveryFailure = new InvalidOperationException("Injected reconciliation failure");
                releaseRecovery.SetException(recoveryFailure);
                Assert.Same(recoveryFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => recoveringRead));
            }

            Assert.Equal(cancellation.Token, Assert.Single(receiver.RecoveryTokens));
            await source.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            Assert.Equal(2, scenario.Idle.LastSafePartitionToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, null);

            receiver.OnRecovery = _ => Task.CompletedTask;
            Assert.False(await scenario.Accessor.ReadFromQueue(queue, source, 1000));
            Assert.Equal(new[] { cancellation.Token, CancellationToken.None }, receiver.RecoveryTokens);
            await source.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            await AssertReportedPartitionPrefix(scenario.Accessor, scenario.Checkpoints, 2);
        }
        finally
        {
            cancellation.Cancel();
            releaseRecovery.TrySetResult();
            await ((Task)recoveringRead).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointProgress_RetainedReadRetriesAccountingWithoutDuplicateAdmission(bool failAfterAdmission)
    {
        var cache = new CheckpointAdmissionCache
        {
            FailNextAdmission = !failAfterAdmission,
            FailNextRegistrationCursor = failAfterAdmission,
        };
        var discovery = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>())
            .Returns(discovery.Task);
        var queue = QueueId.GetQueueId("queue", 0u, 0u);
        var stream = StreamId.Create("admission", Guid.NewGuid());
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        IList<IBatchContainer> messages =
        [
            new TestBatchContainer(stream, new EventSequenceTokenV2(1)),
            new TestBatchContainer(stream, new EventSequenceTokenV2(2)),
        ];
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(messages));
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var agent = CreateAgent(pubSub, queue, receiver, adapterCache);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        try
        {
            Assert.Same(cache.Failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => accessor.ReadFromQueue(queue, receiver, 1000)));
            Assert.Equal(1, cache.AdmissionAttempts);
            Assert.Equal(failAfterAdmission ? 2 : 0, cache.Size);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, null);

            Assert.True(await accessor.ReadFromQueue(queue, receiver, 1000));
            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            Assert.Equal(failAfterAdmission ? 1 : 2, cache.AdmissionAttempts);
            Assert.Equal(2, cache.Size);
            Assert.All(cache.AdmittedLists, admitted => Assert.Same(messages, admitted));
            using var cursor = cache.GetCacheCursor(stream, new EventSequenceTokenV2(1));
            var cachedSequences = new List<long>();
            while (cursor.MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success)
            {
                cachedSequences.Add(cursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
                ((IQueueCacheCursorProgress)cursor).RecordDeliveryCompletion();
            }

            Assert.Equal(new long[] { 1, 2 }, cachedSequences);
            var registeredStream = Assert.Single(await accessor.GetPubSubCache()).Value;
            var registrationTask = Assert.IsAssignableFrom<Task>(registeredStream.RegistrationTask);
            Assert.NotNull(registeredStream.RegistrationCursor);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, null);

            discovery.SetResult(new HashSet<PubSubSubscriptionState>());
            await registrationTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(registeredStream.StreamRegistered);
            Assert.Null(registeredStream.RegistrationCursor);
            Assert.Equal(2, registeredStream.LastReadToken?.SequenceNumber);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, 2);
        }
        finally
        {
            discovery.TrySetResult(new HashSet<PubSubSubscriptionState>());
            await accessor.Shutdown();
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task CheckpointProgress_EmptySubscriptionDiscoveryRetainsPinAcrossRegistrationRetry()
    {
        var cache = new CheckpointAdmissionCache();
        var firstDiscovery = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDiscovery = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationCalls = 0;
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++registrationCalls == 1)
                {
                    firstStarted.TrySetResult();
                    return firstDiscovery.Task;
                }

                secondStarted.TrySetResult();
                return secondDiscovery.Task;
            });
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Injected exhausted registration budget"));
        var queue = QueueId.GetQueueId("queue", 0u, 0u);
        var stream = StreamId.Create("discovery", Guid.NewGuid());
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<IBatchContainer>>(
            [
                new TestBatchContainer(stream, new EventSequenceTokenV2(1)),
                new TestBatchContainer(stream, new EventSequenceTokenV2(2)),
            ]), Task.FromResult<IList<IBatchContainer>>([]));
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var agent = CreateAgent(pubSub, queue, receiver, adapterCache, deliveryBackoff: backoff);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        try
        {
            Assert.True(await accessor.ReadFromQueue(queue, receiver, 1000));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var registeredStream = Assert.Single(await accessor.GetPubSubCache()).Value;
            var firstRegistration = Assert.IsAssignableFrom<Task>(registeredStream.RegistrationTask);
            var pin = Assert.IsAssignableFrom<IQueueCacheCursor>(registeredStream.RegistrationCursor);
            Assert.False(await accessor.ReadFromQueue(queue, receiver, 1000));
            Assert.Equal(1, registrationCalls);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, null);

            firstDiscovery.SetException(new InvalidOperationException("Injected discovery failure"));
            await firstRegistration.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(registeredStream.StreamRegistered);
            Assert.Null(registeredStream.RegistrationTask);
            Assert.Same(pin, registeredStream.RegistrationCursor);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, null);

            Assert.False(await accessor.ReadFromQueue(queue, receiver, 1000));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var secondRegistration = Assert.IsAssignableFrom<Task>(registeredStream.RegistrationTask);
            Assert.Same(pin, registeredStream.RegistrationCursor);
            Assert.Equal(2, registrationCalls);
            Assert.Equal(1, cache.AdmissionAttempts);
            Assert.Equal(2, cache.Size);
            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            await AssertReportedPartitionPrefix(accessor, cache.Progress, null);

            secondDiscovery.SetResult(new HashSet<PubSubSubscriptionState>());
            await secondRegistration.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(registeredStream.StreamRegistered);
            Assert.Empty(registeredStream.AllConsumers());
            Assert.Null(registeredStream.RegistrationCursor);
            Assert.Null(registeredStream.RegistrationTask);
            backoff.Received(1).Next(Arg.Any<int>());
            await AssertReportedPartitionPrefix(accessor, cache.Progress, 2);
            Assert.False(await accessor.ReadFromQueue(queue, receiver, 1000));
            await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            Assert.Equal(2, registrationCalls);
            await AssertReportedPartitionPrefix(accessor, cache.Progress, 2);
        }
        finally
        {
            firstDiscovery.TrySetResult(new HashSet<PubSubSubscriptionState>());
            secondDiscovery.TrySetResult(new HashSet<PubSubSubscriptionState>());
            await accessor.Shutdown();
        }
    }

    private static IEnumerable<long> CheckpointBatchSequences(IBatchContainer batch)
        => (batch is BatchContainerBatch grouped ? grouped.BatchContainers : [batch])
            .Select(item => item.SequenceToken.SequenceNumber);

    private static async Task AssertReportedPartitionPrefix(
        PersistentStreamPullingAgent.ITestAccessor accessor,
        List<StreamSequenceToken?> checkpoints,
        long? expected)
    {
        checkpoints.Clear();
        await accessor.ReportDeliveryProgress();
        if (expected is { } sequence)
        {
            Assert.Equal(sequence, Assert.Single(checkpoints)?.SequenceNumber);
        }
        else
        {
            Assert.Empty(checkpoints);
        }
    }

    private sealed class CheckpointRecovery
    {
        public Func<CancellationToken, Task> OnRecovery { get; set; } = _ => Task.CompletedTask;
        public List<CancellationToken> RecoveryTokens { get; } = [];

        public Task RecoverReadAsync(CancellationToken cancellationToken)
        {
            RecoveryTokens.Add(cancellationToken);
            return OnRecovery(cancellationToken);
        }
    }

    private sealed class CheckpointAdmissionCache : ITestCheckpointingQueueCache
    {
        public Func<CancellationToken, Task>? Recovery { get; set; }
        private readonly IQueueCache inner = new SimpleQueueCache(256, NullLogger.Instance);

        public InvalidOperationException Failure { get; } = new("Injected cache accounting failure");
        public bool FailNextAdmission { get; init; }
        public bool FailNextRegistrationCursor { get; set; }
        public int AdmissionAttempts { get; private set; }
        public int Size => ((SimpleQueueCache)inner).Size;
        public List<IList<IBatchContainer>> AdmittedLists { get; } = [];
        public List<StreamSequenceToken?> Progress { get; } = [];

        public int GetMaxAddCount() => inner.GetMaxAddCount();
        public bool IsUnderPressure() => inner.IsUnderPressure();
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            => inner.TryPurgeFromCache(out purgedItems!);

        public void AddToCache(IList<IBatchContainer> messages)
        {
            AdmissionAttempts++;
            AdmittedLists.Add(messages);
            if (FailNextAdmission && AdmissionAttempts == 1)
            {
                throw Failure;
            }

            inner.AddToCache(messages);
        }

        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            if (FailNextRegistrationCursor)
            {
                FailNextRegistrationCursor = false;
                throw Failure;
            }

            return inner.GetCacheCursor(streamId, token);
        }

        public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
            => Progress.Add(earliestSubscriptionToken);
    }
}
