using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

#pragma warning disable CS0618 // Preserve the receipt-cache surface in migration fixtures.

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
        var agent = CreateAgent(pubSub, queue, receiver, adapterCache, wrapCheckpointReceiver: false);
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
        var agent = CreateAgent(pubSub, QueueId.GetQueueId("legacy", 0, 0), receiver, adapterCache, wrapCheckpointReceiver: false);
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
    [InlineData("implicit", false)]
    [InlineData("inherited", false)]
    [InlineData("wrapped", false)]
    [InlineData("explicit", false)]
    [InlineData("implicit", true)]
    [InlineData("inherited", true)]
    [InlineData("wrapped", true)]
    [InlineData("explicit", true)]
    public void CheckpointContracts_ReleasedCallbacksRemainSupported(string implementation, bool supportsRecovery)
    {
        var wrappedCache = new LegacyCheckpointContractCache();
        ReceiptCheckpointContractCache cache = implementation switch
        {
            "implicit" => new LegacyCheckpointContractCache(),
            "inherited" => new InheritedLegacyCheckpointContractCache(),
            "wrapped" => new WrappedLegacyCheckpointContractCache(wrappedCache),
            "explicit" => new ExplicitLegacyCheckpointContractCache(),
            _ => throw new ArgumentOutOfRangeException(nameof(implementation)),
        };
        var receiver = supportsRecovery
            ? Substitute.For<IQueueAdapterReceiver, IQueueAdapterReceiverReadRecovery>()
            : Substitute.For<IQueueAdapterReceiver>();

        PersistentStreamPullingAgent.ValidateCheckpointingProvider(cache, receiver);
        ((IQueueCache)cache).UpdateDeliveryProgress(null, DateTime.UnixEpoch);
        Assert.Equal(1, cache.CallbackInvocations);
        Assert.Equal(implementation == "wrapped" ? 1 : 0, wrappedCache.CallbackInvocations);
        Assert.Empty(cache.Operations.ReceivedCalls());
        Assert.Empty(wrappedCache.Operations.ReceivedCalls());
        Assert.Empty(receiver.ReceivedCalls());
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData("different-name")]
    [InlineData("different-return")]
    [InlineData("generic")]
    [InlineData("derived-token")]
    [InlineData("different-time")]
    [InlineData("reversed-parameters")]
    [InlineData("extra-parameter")]
    [InlineData("static")]
    [InlineData("private")]
    public void CheckpointContracts_UnrelatedCallbackShapesRemainReceiptBased(string shape)
    {
        ReceiptCheckpointContractCache cache = shape switch
        {
            "different-name" => new RenamedCheckpointContractCache(),
            "different-return" => new ReturningCheckpointContractCache(),
            "generic" => new GenericCheckpointContractCache(),
            "derived-token" => new DerivedTokenCheckpointContractCache(),
            "different-time" => new DifferentTimeCheckpointContractCache(),
            "reversed-parameters" => new ReversedCheckpointContractCache(),
            "extra-parameter" => new ExtraParameterCheckpointContractCache(),
            "static" => new StaticCheckpointContractCache(),
            "private" => new PrivateCheckpointContractCache(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var receiver = Substitute.For<IQueueAdapterReceiver>();

        var exception = Record.Exception(
            () => PersistentStreamPullingAgent.ValidateCheckpointingProvider(cache, receiver));

        Assert.Null(exception);
        Assert.Equal(0, cache.CallbackInvocations);
        Assert.Empty(cache.Operations.ReceivedCalls());
        Assert.Empty(receiver.ReceivedCalls());
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointContracts_ReceiptProvidersDoNotRequireReadRecovery(bool withoutCache)
    {
        var cache = withoutCache ? null : new ReceiptCheckpointContractCache();
        var receiver = Substitute.For<IQueueAdapterReceiver>();

        var exception = Record.Exception(
            () => PersistentStreamPullingAgent.ValidateCheckpointingProvider(cache, receiver));

        Assert.Null(exception);
        Assert.Empty(receiver.ReceivedCalls());
        if (cache is not null)
        {
            Assert.Empty(cache.Operations.ReceivedCalls());
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void CheckpointContracts_MarkerRequiresReceiverRecoveryBeforeProviderOperations()
    {
        // Stronger requirements apply only to a provider explicitly selecting the new contract.
        var cache = new MarkedCheckpointContractCache();
        var receiptReceiver = Substitute.For<IQueueAdapterReceiver>();

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => PersistentStreamPullingAgent.ValidateCheckpointingProvider(cache, receiptReceiver));

        Assert.Contains(receiptReceiver.GetType().FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IQueueAdapterReceiverReadRecovery), exception.Message, StringComparison.Ordinal);
        Assert.Empty(receiptReceiver.ReceivedCalls());
        Assert.Equal(0, cache.CallbackInvocations);
        Assert.Empty(cache.Operations.ReceivedCalls());

        var recoverableReceiver = Substitute.For<IQueueAdapterReceiver, IQueueAdapterReceiverReadRecovery>();
        Assert.Null(Record.Exception(
            () => PersistentStreamPullingAgent.ValidateCheckpointingProvider(cache, recoverableReceiver)));
        Assert.Empty(recoverableReceiver.ReceivedCalls());
        Assert.Equal(0, cache.CallbackInvocations);
        Assert.Empty(cache.Operations.ReceivedCalls());
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

    private sealed class MarkedCheckpointContractCache : LegacyCheckpointContractCache, ICheckpointingQueueCache;

    private sealed class RenamedCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void ReportDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow)
            => CallbackInvocations++;
    }

    private sealed class ReturningCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public int UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow)
            => ++CallbackInvocations;
    }

    private sealed class GenericCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void UpdateDeliveryProgress<T>(StreamSequenceToken safeToken, DateTime utcNow)
            => CallbackInvocations++;
    }

    private sealed class DerivedTokenCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void UpdateDeliveryProgress(EventSequenceTokenV2 safeToken, DateTime utcNow)
            => CallbackInvocations++;
    }

    private sealed class DifferentTimeCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTimeOffset utcNow)
            => CallbackInvocations++;
    }

    private sealed class ReversedCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void UpdateDeliveryProgress(DateTime utcNow, StreamSequenceToken safeToken)
            => CallbackInvocations++;
    }

    private sealed class ExtraParameterCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow, bool force)
            => CallbackInvocations++;
    }

    private sealed class StaticCheckpointContractCache : ReceiptCheckpointContractCache
    {
        public static void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow)
            => throw new InvalidOperationException("A same-name static method is not a provider callback.");
    }

    private sealed class PrivateCheckpointContractCache : ReceiptCheckpointContractCache
    {
        private void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow)
            => CallbackInvocations++;
    }

    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointContracts_RejectedStartupCleansResourcesBeforeSourceInitialization(
        bool markedCache, bool cleanupFails)
    {
        var cleanupOrder = new List<string>();
        var cleanupFailure = new InvalidOperationException("Injected provider cleanup failure");
        void DisposeCache()
        {
            cleanupOrder.Add("cache");
            if (cleanupFails)
            {
                throw cleanupFailure;
            }
        }

        DisposableLegacyCheckpointContractCache cache = markedCache
            ? new DisposableMarkedCheckpointContractCache(DisposeCache)
            : new DisposableLegacyCheckpointContractCache(DisposeCache);
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        var options = new StreamPullingAgentOptions { InitQueueTimeout = TimeSpan.FromSeconds(17) };
        receiver.Shutdown(options.InitQueueTimeout, CancellationToken.None).Returns(_ =>
        {
            cleanupOrder.Add("receiver");
            return cleanupFails ? Task.FromException(cleanupFailure) : Task.CompletedTask;
        });
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var queue = QueueId.GetQueueId("checkpoint-contract", 0u, 0u);
        var agent = CreateAgent(
            Substitute.For<IStreamPubSub>(), queue, receiver, adapterCache,
            options: options, wrapCheckpointReceiver: false);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        receiver.ClearReceivedCalls();

        try
        {
            var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(() => InitializeAgent(agent));

            var expectedMessage = markedCache
                ? $"Checkpointing receiver {receiver.GetType().FullName} must implement {nameof(IQueueAdapterReceiverReadRecovery)}."
                : $"Queue cache {cache.GetType().FullName} implements the legacy delivery-progress callback. "
                    + $"Migrate it to {nameof(ICheckpointingQueueCache)} and certified cursor progress.";
            Assert.Equal(expectedMessage, exception.Message);
            Assert.Equal(new[] { "receiver", "cache" }, cleanupOrder);
            Assert.Equal(1, cache.DisposeCount);
            Assert.Equal(0, cache.CallbackInvocations);
            Assert.Empty(cache.Operations.ReceivedCalls());

            // Shutdown is the only receiver operation: neither initialization overload nor
            // a source read may run before configuration validation has succeeded.
            var shutdownCall = Assert.Single(receiver.ReceivedCalls());
            Assert.Equal(nameof(IQueueAdapterReceiver.Shutdown), shutdownCall.GetMethodInfo().Name);
            Assert.Collection(shutdownCall.GetArguments(),
                argument => Assert.Equal(options.InitQueueTimeout, Assert.IsType<TimeSpan>(argument)),
                argument => Assert.Equal(CancellationToken.None, Assert.IsType<CancellationToken>(argument)));

            // Rejected resources must be detached, not retained for a later pump or cleanup.
            await accessor.RunQueuePump(queue, TestContext.Current.CancellationToken);
            await accessor.Shutdown();
            Assert.Single(receiver.ReceivedCalls());
            Assert.Equal(1, cache.DisposeCount);
            Assert.Equal(new[] { "receiver", "cache" }, cleanupOrder);
        }
        finally
        {
            await accessor.Shutdown();
        }
    }

    private class DisposableLegacyCheckpointContractCache(Action onDispose)
        : LegacyCheckpointContractCache, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            onDispose();
        }
    }

    private sealed class DisposableMarkedCheckpointContractCache(Action onDispose)
        : DisposableLegacyCheckpointContractCache(onDispose), ICheckpointingQueueCache;
}
