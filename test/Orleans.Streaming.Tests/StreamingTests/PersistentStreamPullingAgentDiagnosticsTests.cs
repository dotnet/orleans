using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

public partial class PersistentStreamPullingAgentTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableConsumerReportsUnregistrationOutcomeWithoutBlockingDelivery(bool failUnregistration)
    {
        var streamId = new QualifiedStreamId("provider", StreamId.Create("unregister", Guid.NewGuid()));
        var subscriptionId = GuidId.GetGuidId(Guid.NewGuid());
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        var token = new EventSequenceTokenV2(1);
        var cache = new ScriptedQueueCache();
        cache.AddToCache([new TestBatchContainer(streamId.StreamId, token)]);
        var (accessor, pubSub, streamData) = await CreateInitializedAgentWithStream(
            streamId, token, cache, new StreamPullingAgentOptions());
        var unavailable = Tester.ClientConnectionTests.ClientObserverRoutingTests.CreateUnavailableClientException();
        var consumer = new UnavailableConsumer(unavailable);
        var data = streamData.AddConsumer(subscriptionId, streamId, consumer, filterData: null, DateTime.UtcNow);
        data.Cursor = cache.GetCacheCursor(streamId.StreamId, token);
        data.IsRegistered = true;
        var unregister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pubSub.UnregisterConsumer(subscriptionId, streamId, Arg.Any<CancellationToken>()).Returns(unregister.Task);
        var events = new ConcurrentQueue<StreamingEvents.StreamingEvent>();
        var outcome = new TaskCompletionSource<StreamingEvents.SubscriptionUnregistration>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = StreamingEvents.AllEvents.Subscribe(value =>
        {
            if (value is StreamingEvents.MessageDeliveryFailed failed && failed.SubscriptionId == subscriptionId.Guid)
            {
                events.Enqueue(value);
            }
            else if (value is StreamingEvents.SubscriptionUnregistration registration && registration.SubscriptionId == subscriptionId.Guid)
            {
                events.Enqueue(value);
                if (registration.Stage != StreamingEvents.SubscriptionUnregistrationStage.Requested)
                {
                    outcome.TrySetResult(registration);
                }
            }
        });

        try
        {
            await accessor.RunConsumerCursor(data).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var failed = Assert.Single(events.OfType<StreamingEvents.MessageDeliveryFailed>());
            Assert.Same(unavailable, failed.Exception);
            Assert.Same(consumer, failed.Consumer);
            Assert.Equal(token, failed.SequenceToken);
            Assert.Equal(streamId.StreamId, failed.StreamId);
            Assert.Equal(streamId.ProviderName, failed.StreamProvider);
            Assert.Equal(siloAddress, failed.SiloAddress);
            var requested = Assert.Single(events.OfType<StreamingEvents.SubscriptionUnregistration>());
            Assert.Equal(StreamingEvents.SubscriptionUnregistrationStage.Requested, requested.Stage);
            Assert.Equal(streamId.ProviderName, requested.StreamProvider);
            Assert.Equal(streamId.StreamId, requested.StreamId);
            Assert.Equal(siloAddress, requested.SiloAddress);
            Assert.Same(consumer, requested.Consumer);
            Assert.Null(requested.Exception);
            Assert.False(outcome.Task.IsCompleted);

            var exception = new InvalidOperationException("unregistration storage failure");
            if (failUnregistration)
            {
                unregister.SetException(exception);
            }
            else
            {
                unregister.SetResult();
            }

            var completed = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(
                failUnregistration ? StreamingEvents.SubscriptionUnregistrationStage.Failed : StreamingEvents.SubscriptionUnregistrationStage.Completed,
                completed.Stage);
            Assert.Equal(streamId.StreamId, completed.StreamId);
            Assert.Equal(streamId.ProviderName, completed.StreamProvider);
            Assert.Equal(siloAddress, completed.SiloAddress);
            Assert.Same(consumer, completed.Consumer);
            Assert.Same(failUnregistration ? exception : null, completed.Exception);
            _ = pubSub.Received(1).UnregisterConsumer(subscriptionId, streamId, Arg.Any<CancellationToken>());
        }
        finally
        {
            unregister.TrySetResult();
            await accessor.Shutdown();
        }
    }

    private sealed class UnavailableConsumer(ClientNotAvailableException exception) : IStreamConsumerExtension
    {
        public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item,
            StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item,
            StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item,
            StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => Task.FromException<StreamHandshakeToken?>(exception);

        public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ErrorInStream(GuidId subscriptionId, Exception error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
            => Task.FromResult<StreamHandshakeToken?>(null);
    }
}
