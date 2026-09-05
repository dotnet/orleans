using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;
using RecoveryBatch = UnitTests.StreamingTests.RecoverableStreamReceiverTests.TestBatchContainer;
using RecoveryMessage = UnitTests.StreamingTests.RecoverableStreamReceiverTests.TestQueueMessage;

namespace UnitTests.StreamingTests;

public partial class PersistentStreamPullingAgentTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FutureStartToken_BeyondFiniteCapacity_PreservesDeliveryBarriers(bool implicitSubscription)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(10);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var options = new StreamPullingAgentOptions();
        var queueId = QueueId.GetQueueId("queue", 0u, 0u);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        var qualifiedStream = new QualifiedStreamId("provider", stream);
        var qualifiedOtherStream = new QualifiedStreamId("provider", otherStream);
        var targetSubscription = GuidId.GetGuidId(implicitSubscription
            ? SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())
            : SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
        var slowSubscription = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
        var source = new BoundedRecoverySource(
            Enumerable.Range(1, 9)
                .Select(sequence => new RecoveryMessage(
                    sequence is 2 or 4 or 6 or 9 ? otherStream : stream,
                    sequence,
                    $"payload-{sequence}"))
                .ToList());
        var adapter = new RecoverableStreamReceiverTests.TestDataAdapter();
        var cache = new RecoverableStreamQueueCache<RecoveryMessage>(
            3,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4 * 1024)),
            adapter,
            new RecoverableStreamReceiverTests.NoOpEvictionStrategy(),
            NullLogger.Instance,
            maxCacheSize: 3);
        var checkpoints = new List<long>();
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        checkpointer.CheckpointExists.Returns(true);
        checkpointer.Load(Arg.Any<CancellationToken>()).Returns(Task.FromResult("0"));
        checkpointer.When(value => value.Update(
                Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()))
            .Do(call => checkpoints.Add(long.Parse(call.Arg<string>(), CultureInfo.InvariantCulture)));
        var receiver = new RecoverableStreamReceiver<RecoveryMessage>(
            source, adapter, cache, checkpointer, startFromNow: false);
        var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
        queueAdapterCache.CreateQueueCache(queueId).Returns(receiver);
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, cancellationToken)
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache, timeProvider, options);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        var firstDelivery = new TaskCompletionSource<IBatchContainer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelivery = new TaskCompletionSource<IBatchContainer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryResult = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredTokens = new List<long>();
        var attempts = 0;
        var firstExpectedToken = implicitSubscription ? 8 : 7;
        var targetConsumer = new RecoveryConsumer(
            StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(7)),
            batch =>
            {
                deliveredTokens.Add(batch.SequenceToken.SequenceNumber);
                if (batch.SequenceToken.SequenceNumber != firstExpectedToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(null);
                }

                if (++attempts == 1)
                {
                    firstDelivery.SetResult(batch);
                    return firstResult.Task;
                }

                retryDelivery.SetResult(batch);
                return retryResult.Task;
            });
        var slowConsumer = new RecordingConsumer(StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(5)));
        using var diagnostics = new DiagnosticEventCollector(StreamingEvents.ListenerName);

        await InitializeAgent(agent);
        try
        {
            Assert.Equal("0", source.StartPosition.Checkpoint);
            await accessor.RegisterStream(qualifiedStream, new EventSequenceTokenV2(1), timeProvider.GetUtcNow().UtcDateTime);
            await accessor.RegisterStream(qualifiedOtherStream, new EventSequenceTokenV2(1), timeProvider.GetUtcNow().UtcDateTime);
            var streamData = (await accessor.GetPubSubCache())[qualifiedStream];
            var targetData = streamData.AddConsumer(
                targetSubscription, qualifiedStream, targetConsumer, filterData: null, timeProvider.GetUtcNow().UtcDateTime);
            var slowData = streamData.AddConsumer(
                slowSubscription, qualifiedStream, slowConsumer, filterData: null, timeProvider.GetUtcNow().UtcDateTime);
            Assert.True(await accessor.DoHandshakeWithConsumer(targetData, new EventSequenceTokenV2(1)));
            Assert.True(await accessor.DoHandshakeWithConsumer(slowData, new EventSequenceTokenV2(1)));
            targetData.IsRegistered = true;
            slowData.IsRegistered = true;

            await UpdateProgress();
            Assert.Empty(checkpoints);
            await accessor.RunQueuePump(queueId, cancellationToken);
            await accessor.GetPubSubCache();
            Assert.Equal(1, source.ReadCount);
            Assert.Equal(3, source.AdmittedThrough);
            Assert.Equal(3, cache.ItemCount);
            Assert.Equal(0, receiver.GetMaxAddCount());
            Assert.Equal(3, targetData.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(3, slowData.LastSafePartitionToken?.SequenceNumber);
            Assert.Empty(deliveredTokens);
            Assert.Empty(slowConsumer.DeliveredTokens);

            await UpdateProgress();
            Assert.Equal(3, Assert.Single(checkpoints));
            Assert.Equal(0, cache.ItemCount);
            Assert.Equal(3, receiver.GetMaxAddCount());

            await accessor.RunQueuePump(queueId, cancellationToken);
            await WaitForPhase(slowConsumer.Delivered.Task, "slow consumer receives record 5");
            await accessor.GetPubSubCache();
            Assert.Equal(2, source.ReadCount);
            Assert.Equal(6, source.AdmittedThrough);
            Assert.Equal(5, Assert.Single(slowConsumer.DeliveredTokens).SequenceNumber);
            Assert.Equal(6, targetData.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(3, slowData.LastSafePartitionToken?.SequenceNumber);
            Assert.Empty(deliveredTokens);

            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3 }, checkpoints);
            Assert.Equal(3, cache.ItemCount);
            Assert.True(receiver.IsUnderPressure());
            await accessor.RunQueuePump(queueId, cancellationToken);
            Assert.Equal(2, source.ReadCount);

            var slowDrained = WaitForDrain(slowSubscription);
            slowConsumer.ReleaseDelivery();
            await slowDrained;
            await accessor.GetPubSubCache();
            Assert.Equal(6, slowData.LastSafePartitionToken?.SequenceNumber);
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 6 }, checkpoints);
            Assert.Equal(0, cache.ItemCount);
            Assert.Equal(3, receiver.GetMaxAddCount());

            var targetDrained = WaitForDrain(targetSubscription);
            await accessor.RunQueuePump(queueId, cancellationToken);
            var pendingBatch = Assert.IsType<RecoveryBatch>(
                await WaitForPhase(firstDelivery.Task, $"recovery consumer receives record {firstExpectedToken}"));
            await accessor.GetPubSubCache();
            Assert.Equal(firstExpectedToken, pendingBatch.SequenceToken.SequenceNumber);
            Assert.Equal($"payload-{firstExpectedToken}", pendingBatch.Payload);
            Assert.Equal(3, source.ReadCount);
            Assert.Equal(9, source.AdmittedThrough);
            Assert.Equal(9, slowData.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(new long[] { 5, 7, 8 }, slowConsumer.DeliveredTokens.Select(token => token.SequenceNumber));
            Assert.Null(targetData.LastProcessedToken);
            var safeBeforeDelivery = implicitSubscription ? 7 : 6;
            Assert.Equal(safeBeforeDelivery, targetData.LastSafePartitionToken?.SequenceNumber);

            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 6, safeBeforeDelivery }, checkpoints);
            Assert.Equal(9 - safeBeforeDelivery, cache.ItemCount);
            Assert.Equal(safeBeforeDelivery.ToString(CultureInfo.InvariantCulture), cache.LastPurgedOffset);

            firstResult.SetException(new InvalidOperationException("Retry the pending recovery delivery."));
            var retriedBatch = await WaitForPhase(retryDelivery.Task, $"recovery consumer retries record {firstExpectedToken}");
            Assert.Same(pendingBatch, retriedBatch);
            await accessor.GetPubSubCache();
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 6, safeBeforeDelivery, safeBeforeDelivery }, checkpoints);
            Assert.Equal(9 - safeBeforeDelivery, cache.ItemCount);
            Assert.Null(targetData.LastProcessedToken);

            retryResult.SetResult(null);
            await targetDrained;
            await accessor.GetPubSubCache();
            Assert.Equal(8, targetData.LastProcessedToken?.SequenceNumber);
            Assert.Equal(9, targetData.LastSafePartitionToken?.SequenceNumber);
            Assert.Equal(implicitSubscription ? new long[] { 8, 8 } : new long[] { 7, 7, 8 }, deliveredTokens);
            Assert.Empty(targetConsumer.Errors);
            Assert.Empty(slowConsumer.Errors);
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 6, safeBeforeDelivery, safeBeforeDelivery, 9 }, checkpoints);
            Assert.Equal("9", cache.LastPurgedOffset);
            Assert.Equal(0, cache.ItemCount);
            Assert.Equal(3, receiver.GetMaxAddCount());
        }
        finally
        {
            firstResult.TrySetResult(null);
            retryResult.TrySetResult(null);
            slowConsumer.ReleaseDelivery();
            await accessor.Shutdown();
        }

        Task<DiagnosticEvent> WaitForDrain(GuidId subscription)
            => WaitForPhase(
                diagnostics.WaitForEventAsync(
                    nameof(StreamingEvents.ConsumerCursorDrained),
                    entry => entry.Payload is StreamingEvents.ConsumerCursorDrained drained
                        && drained.StreamId == stream && drained.SubscriptionId == subscription.Guid,
                    timeout,
                    cancellationToken),
                $"subscription {subscription} drains");

        async Task<T> WaitForPhase<T>(Task<T> task, string phase)
        {
            try
            {
                return await task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {phase} on {qualifiedStream}; implicit={implicitSubscription}, "
                    + $"reads={source.ReadCount}, admitted={source.AdmittedThrough}, cached={cache.ItemCount}, "
                    + $"checkpoints=[{string.Join(", ", checkpoints)}].",
                    exception);
            }
        }

        async Task UpdateProgress()
        {
            timeProvider.Advance(options.DeliveryProgressUpdateInterval);
            await accessor.GetPubSubCache();
        }
    }

    private sealed class RecoveryConsumer(
        StreamHandshakeToken? startToken,
        Func<IBatchContainer, Task<StreamHandshakeToken?>> deliverBatch) : IStreamConsumerExtension
    {
        public List<Exception> Errors { get; } = [];

        public Task<StreamHandshakeToken?> DeliverImmutable(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            object item,
            StreamSequenceToken currentToken,
            StreamHandshakeToken? handshakeToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverMutable(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            object item,
            StreamSequenceToken currentToken,
            StreamHandshakeToken? handshakeToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverBatch(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            IBatchContainer item,
            StreamHandshakeToken? handshakeToken,
            CancellationToken cancellationToken) => deliverBatch(item);

        public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
        {
            Errors.Add(exc);
            return Task.CompletedTask;
        }

        public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
            => Task.FromResult<StreamHandshakeToken?>(startToken);
    }

    private sealed class BoundedRecoverySource(IReadOnlyList<RecoveryMessage> messages)
        : IRecoverableStreamSource<RecoveryMessage>
    {
        public RecoverableStreamStartPosition StartPosition { get; private set; }
        public long AdmittedThrough { get; private set; }
        public int ReadCount { get; private set; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartPosition = position;
            AdmittedThrough = long.Parse(position.Checkpoint!, CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecoveryMessage>> Read(int maxCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult<IReadOnlyList<RecoveryMessage>>(
                messages.Where(message => message.SequenceNumber > AdmittedThrough).Take(maxCount).ToList());
        }

        public void MessagesAdded(IReadOnlyList<RecoveryMessage> admitted)
            => AdmittedThrough = admitted[^1].SequenceNumber;

        public Task Shutdown(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
