using NSubstitute;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

#pragma warning disable CS0618 // Exercise released provider implementations.

namespace UnitTests.StreamingTests;

public partial class PersistentStreamPullingAgentTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task CheckpointContracts_LegacyAdmissionDoesNotRequireAtomicRetry()
    {
        var stream = StreamId.Create("legacy", "admission");
        var admitted = new List<long>();
        var cache = Substitute.For<IQueueCache>();
        cache.GetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(new EmptyQueueCacheCursor());
        cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
            .Returns(_ => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
        cache.When(value => value.AddToCache(Arg.Any<IList<IBatchContainer>>())).Do(call =>
        {
            var batch = Assert.Single(call.Arg<IList<IBatchContainer>>());
            admitted.Add(batch.SequenceToken.SequenceNumber);
            if (admitted.Count == 1) throw new InvalidOperationException("Legacy partial admission");
        });
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(stream, new EventSequenceTokenV2(1))]),
            Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(stream, new EventSequenceTokenV2(2))]));
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var queue = QueueId.GetQueueId("legacy", 0, 0);
        var agent = CreateAgent(pubSub, queue, receiver, adapterCache);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => accessor.ReadFromQueue(queue, receiver, 10));
            Assert.True(await accessor.ReadFromQueue(queue, receiver, 10));
            Assert.Equal(new long[] { 1, 2 }, admitted);
            await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await accessor.Shutdown();
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointContracts_LegacySnapshotsPreserveReleasedTokensAndNull(bool subscribed)
    {
        var cache = new LegacyCheckpointContractCache();
        cache.Operations.GetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(new EmptyQueueCacheCursor());
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var agent = CreateAgent(pubSub, QueueId.GetQueueId("legacy", 0, 0), receiver, adapterCache);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        if (subscribed)
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("legacy", "snapshot"));
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            var stream = (await accessor.GetPubSubCache())[streamId];
            foreach (var sequence in new long[] { 200, 95 })
            {
                var consumer = stream.AddConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, null!, null, DateTime.UtcNow);
                consumer.IsRegistered = true;
                consumer.LastProcessedToken = new EventSequenceTokenV2(sequence);
            }
        }

        await accessor.Shutdown();
        Assert.Equal(1, cache.CallbackInvocations);
        Assert.Equal(subscribed ? 95L : (long?)null, cache.LastReportedToken?.SequenceNumber);
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData("default")]
    [InlineData("implicit")]
    [InlineData("inherited")]
    [InlineData("wrapped")]
    [InlineData("explicit")]
    public async Task CheckpointContracts_ReleasedCallbacksRemainSupported(string implementation)
    {
        var wrappedCache = new LegacyCheckpointContractCache();
        ReceiptCheckpointContractCache cache = implementation switch
        {
            "default" => new ReceiptCheckpointContractCache(),
            "implicit" => new LegacyCheckpointContractCache(),
            "inherited" => new InheritedLegacyCheckpointContractCache(),
            "wrapped" => new WrappedLegacyCheckpointContractCache(wrappedCache),
            "explicit" => new ExplicitLegacyCheckpointContractCache(),
            _ => throw new ArgumentOutOfRangeException(nameof(implementation)),
        };
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var agent = CreateAgent(
            Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("legacy", 0, 0), receiver, adapterCache);
        await InitializeAgent(agent);
        await ((PersistentStreamPullingAgent.ITestAccessor)agent).Shutdown();

        Assert.Equal(implementation == "default" ? 0 : 1, cache.CallbackInvocations);
        Assert.Equal(implementation == "wrapped" ? 1 : 0, wrappedCache.CallbackInvocations);
        Assert.Empty(cache.Operations.ReceivedCalls());
        Assert.Empty(wrappedCache.Operations.ReceivedCalls());
        await receiver.Received(1).Initialize(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private class ReceiptCheckpointContractCache : IQueueCache
    {
        public IQueueCache Operations { get; } = Substitute.For<IQueueCache>();
        public int CallbackInvocations { get; protected set; }

        public int GetMaxAddCount() => Operations.GetMaxAddCount();
        public void AddToCache(IList<IBatchContainer> messages) => Operations.AddToCache(messages);
        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            => Operations.TryPurgeFromCache(out purgedItems!);
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => Operations.GetCacheCursor(streamId, token);
        public bool IsUnderPressure() => Operations.IsUnderPressure();
    }

    private class LegacyCheckpointContractCache : ReceiptCheckpointContractCache, IQueueCache
    {
        public StreamSequenceToken? LastReportedToken { get; private set; }
        public void UpdateDeliveryProgress(StreamSequenceToken? safeToken, DateTime utcNow)
        {
            CallbackInvocations++;
            LastReportedToken = safeToken;
        }
    }

    private sealed class ExplicitLegacyCheckpointContractCache : ReceiptCheckpointContractCache, IQueueCache
    {
        void IQueueCache.UpdateDeliveryProgress(StreamSequenceToken? token, DateTime utcNow)
            => CallbackInvocations++;
    }

    private sealed class InheritedLegacyCheckpointContractCache : LegacyCheckpointContractCache;

    private sealed class WrappedLegacyCheckpointContractCache(LegacyCheckpointContractCache inner)
        : ReceiptCheckpointContractCache, IQueueCache
    {
        public void UpdateDeliveryProgress(StreamSequenceToken? safeToken, DateTime utcNow)
        {
            CallbackInvocations++;
            inner.UpdateDeliveryProgress(safeToken, utcNow);
        }
    }
}
