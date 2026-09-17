using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

#pragma warning disable CS0618 // Forward the legacy cache API used by the shared test fixtures.

namespace UnitTests.StreamingTests;

public partial class PersistentStreamPullingAgentTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task ReceiptProvider_ExhaustedDeliveryPreservesItsSkipPolicy()
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        await using var scenario = await CreateCheckpointScenario(deliveryBackoff: backoff, checkpointing: false);
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
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task CheckpointProgress_ExhaustedDeliveryRetriesThreeBeforeAcknowledgingFour(bool pooled, int batchSize)
    {
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Injected exhausted delivery budget"));
        await using var scenario = await CreateCheckpointScenario(pooled, batchSize, deliveryBackoff: backoff);
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

            releaseError.SetResult();
            await failedRun.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(originalCursor, scenario.Idle.Cursor);
            Assert.Equal(StreamConsumerDataState.Inactive, scenario.Idle.State);
            failDelivery = false;

            await scenario.Accessor.RunQueuePump(QueueId.GetQueueId("queue", 0u, 0u), TestContext.Current.CancellationToken);

            Assert.Equal(batchSize == 1 ? new long[] { 3, 3, 4 } : new long[] { 3, 4, 3, 4 }, attempts);
            Assert.Equal(new long[] { 3, 4 }, acknowledged);
            backoff.Received(1).Next(Arg.Any<int>());
            Assert.Single(consumer.Errors);
            Assert.Same(originalCursor, scenario.Idle.Cursor);
            Assert.Equal(4, scenario.Idle.LastProcessedToken?.SequenceNumber);
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
                ((IQueueCacheCursorProgress)cursor).RecordDeliverySuccess();
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
