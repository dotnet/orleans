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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LateLatestSubscriber_AtFullCapacity_ReleasesExcludedTailAndPreservesDeliveryBarriers(
        bool explicitLatest,
        bool tailMatchesStream)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(10);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var options = new StreamPullingAgentOptions();
        var queueId = QueueId.GetQueueId("queue", 0u, 0u);
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        var otherStream = StreamId.Create("namespace", Guid.NewGuid());
        var qualifiedStream = new QualifiedStreamId("provider", stream);
        var messages = Enumerable.Range(1, 9)
            .Select(sequence => new RecoveryMessage(
                sequence is 5 or 8 || sequence == 3 && !tailMatchesStream ? otherStream : stream,
                sequence,
                $"payload-{sequence}"))
            .ToArray();
        var source = new BoundedRecoverySource(messages);
        var adapter = new RecoverableStreamReceiverTests.TestDataAdapter();
        using var cache = new RecoverableStreamQueueCache<RecoveryMessage>(
            3,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(4096)),
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
        var receiver = new RecoverableStreamReceiver<RecoveryMessage>(source, adapter, cache, checkpointer, false);
        var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
        queueAdapterCache.CreateQueueCache(queueId).Returns(receiver);
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, cancellationToken)
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache, timeProvider, options);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        var inclusiveConsumer = new RecordingConsumer(StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(1)));
        var firstDelivery = new TaskCompletionSource<IBatchContainer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelivery = new TaskCompletionSource<IBatchContainer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryResult = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<long>();
        var attempts = 0;
        var latestConsumer = new RecoveryConsumer(
            explicitLatest ? StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.Latest) : null,
            batch =>
            {
                delivered.Add(batch.SequenceToken.SequenceNumber);
                if (batch.SequenceToken.SequenceNumber != 4)
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
        using var diagnostics = new DiagnosticEventCollector(StreamingEvents.ListenerName);
        await InitializeAgent(agent);
        try
        {
            await accessor.RegisterStream(qualifiedStream, new EventSequenceTokenV2(1), DateTime.UnixEpoch);
            await accessor.RegisterStream(new QualifiedStreamId("provider", otherStream), new EventSequenceTokenV2(1), DateTime.UnixEpoch);
            var streamData = (await accessor.GetPubSubCache())[qualifiedStream];
            var inclusive = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStream, inclusiveConsumer, null, DateTime.UnixEpoch);
            Assert.True(await accessor.DoHandshakeWithConsumer(inclusive, new EventSequenceTokenV2(1)));
            inclusive.IsRegistered = true;
            await accessor.RunQueuePump(queueId, cancellationToken);
            await inclusiveConsumer.Delivered.Task.WaitAsync(timeout, cancellationToken);
            await accessor.GetPubSubCache();
            Assert.Equal(3, cache.ItemCount);
            Assert.Equal(0, receiver.GetMaxAddCount());
            Assert.Equal(1, source.ReadCount);
            Assert.Null(inclusive.LastSafePartitionToken);

            var latest = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStream, latestConsumer, null, DateTime.UnixEpoch);
            Assert.True(await accessor.DoHandshakeWithConsumer(latest, cacheToken: null));
            latest.IsRegistered = true;
            await accessor.RunConsumerCursor(latest);
            Assert.Equal(StreamConsumerDataState.Inactive, latest.State);
            Assert.Null(latest.LastProcessedToken);
            Assert.Empty(delivered);
            await UpdateProgress();
            Assert.Empty(checkpoints);
            Assert.Equal(3, cache.ItemCount);
            await accessor.RunQueuePump(queueId, cancellationToken);
            Assert.Equal(1, source.ReadCount);

            var inclusiveDrained = WaitForDrain(inclusive.SubscriptionId);
            inclusiveConsumer.ReleaseDelivery();
            await inclusiveDrained;
            await accessor.GetPubSubCache();
            Assert.Equal(tailMatchesStream ? new long[] { 1, 2, 3 } : [1L, 2L],
                inclusiveConsumer.DeliveredTokens.Select(token => token.SequenceNumber));
            await UpdateProgress();
            Assert.Equal(3, Assert.Single(checkpoints));
            Assert.Equal(3, latest.LastSafePartitionToken?.SequenceNumber);
            Assert.Null(latest.LastProcessedToken);
            Assert.Equal(0, cache.ItemCount);
            Assert.Equal(3, receiver.GetMaxAddCount());

            var latestDrained = WaitForDrain(latest.SubscriptionId);
            await accessor.RunQueuePump(queueId, cancellationToken);
            var pending = Assert.IsType<RecoveryBatch>(await firstDelivery.Task.WaitAsync(timeout, cancellationToken));
            await accessor.GetPubSubCache();
            Assert.Equal("payload-4", pending.Payload);
            Assert.Equal(2, source.ReadCount);
            Assert.Equal(6, source.AdmittedThrough);
            Assert.Equal(3, cache.ItemCount);
            Assert.Equal(3, latest.LastSafePartitionToken?.SequenceNumber);
            Assert.Null(latest.LastProcessedToken);
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3 }, checkpoints);
            await accessor.RunQueuePump(queueId, cancellationToken);
            Assert.Equal(2, source.ReadCount);

            firstResult.SetException(new InvalidOperationException("Retry the first included Latest record."));
            Assert.Same(pending, await retryDelivery.Task.WaitAsync(timeout, cancellationToken));
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 3 }, checkpoints);
            Assert.Equal(3, cache.ItemCount);
            retryResult.SetResult(null);
            await latestDrained;
            await UpdateProgress();
            Assert.Equal(new long[] { 3, 3, 3, 6 }, checkpoints);
            Assert.Equal(0, cache.ItemCount);

            latestDrained = WaitForDrain(latest.SubscriptionId);
            await accessor.RunQueuePump(queueId, cancellationToken);
            await latestDrained;
            await UpdateProgress();
            Assert.Equal(3, source.ReadCount);
            Assert.Equal(9, source.AdmittedThrough);
            Assert.Equal(new long[] { 4, 4, 6, 7, 9 }, delivered);
            Assert.Equal(new long[] { 3, 3, 3, 6, 9 }, checkpoints);
            Assert.Equal(0, cache.ItemCount);
            Assert.Equal("9", cache.LastPurgedOffset);
            Assert.Empty(latestConsumer.Errors);
            Assert.Empty(inclusiveConsumer.Errors);
        }
        finally
        {
            inclusiveConsumer.ReleaseDelivery();
            firstResult.TrySetResult(null);
            retryResult.TrySetResult(null);
            await accessor.Shutdown();
        }

        Task<DiagnosticEvent> WaitForDrain(GuidId subscription)
            => diagnostics.WaitForEventAsync(
                nameof(StreamingEvents.ConsumerCursorDrained),
                entry => entry.Payload is StreamingEvents.ConsumerCursorDrained drained
                    && drained.StreamId == stream && drained.SubscriptionId == subscription.Guid,
                timeout,
                cancellationToken);

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
        public long AdmittedThrough { get; private set; }
        public int ReadCount { get; private set; }

        public Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
