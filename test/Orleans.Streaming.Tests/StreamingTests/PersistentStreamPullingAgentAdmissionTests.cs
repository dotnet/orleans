using NSubstitute;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
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
    public async Task Shutdown_ClosesAdmissionAndDrainsPumpBeforeReinitialize(bool failRead)
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<IList<IBatchContainer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueId = QueueId.GetQueueId("queue", 0, 0);
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            readStarted.TrySetResult();
            return read.Task;
        });
        var pubSub = Substitute.For<IStreamPubSubRuntime>();
        var agent = CreateAgent(pubSub, queueId, receiver);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        var pump = accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
        Task? shutdown = null;
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            shutdown = accessor.Shutdown();
            Assert.Empty(await accessor.GetPubSubCache());
            Assert.False(shutdown.IsCompleted);
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("closed", Guid.NewGuid()));
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            await agent.RunOrQueueTask(() => agent.AddSubscriber(
                GuidId.GetNewGuidId(), streamId, GrainId.Create("consumer", "closed"), null, TestContext.Current.CancellationToken));

            Assert.False(pump.IsCompleted);
            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            await receiver.DidNotReceive().Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await pubSub.DidNotReceive().RegisterProducer(
                Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>(), Arg.Any<CancellationToken>());
            Assert.Empty(await accessor.GetPubSubCache());
        }
        finally
        {
            if (failRead)
            {
                read.TrySetException(new InvalidOperationException("Controlled queue read failure."));
            }
            else
            {
                read.TrySetResult([]);
            }

            await Task.WhenAll(shutdown ?? accessor.Shutdown(), pump)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<IBatchContainer>>([]));
        await InitializeAgent(agent);
        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
        await accessor.Shutdown();
        await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await receiver.Received(2).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task Shutdown_DrainsReceiverInitializationAfterCallerCancellation()
    {
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        receiver.Initialize(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            initializationStarted.TrySetResult();
            return initialized.Task;
        });
        var agent = CreateAgent(pubSub: null, QueueId.GetQueueId("queue", 0, 0), receiver);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        using var cancellation = new CancellationTokenSource();
        await agent.RunOrQueueTask(() => agent.Initialize(cancellation.Token));
        Task? shutdown = null;
        try
        {
            await initializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            shutdown = accessor.Shutdown();
            Assert.Empty(await accessor.GetPubSubCache());
            Assert.False(initialized.Task.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            await receiver.DidNotReceive().Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            initialized.TrySetResult();
            await (shutdown ?? accessor.Shutdown()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await receiver.DidNotReceive().GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task Shutdown_DrainsPendingSubscriberHandshake()
    {
        var pubSub = Substitute.For<IStreamPubSubRuntime>();
        pubSub.RegisterProducer(default, default, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var queueId = QueueId.GetQueueId("queue", 0, 0);
        var streamId = new QualifiedStreamId("provider", StreamId.Create("handshake", Guid.NewGuid()));
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        var cache = new RecordingQueueCache();
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(queueId).Returns(cache);
        var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await InitializeAgent(agent);
        await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
        var streamData = Assert.Single(await accessor.GetPubSubCache()).Value;
        var handshakeStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshake = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingConsumer(getSequenceToken: token =>
        {
            handshakeStarted.TrySetResult(token);
            return handshake.Task;
        });
        var subscription = GuidId.GetNewGuidId();
        var data = streamData.AddConsumer(subscription, streamId, consumer, null, DateTime.UtcNow);
        try
        {
            await agent.RunOrQueueTask(() => agent.AddSubscriber(
                subscription, streamId, GrainId.Create("consumer", "handshake"), null, TestContext.Current.CancellationToken));
            var token = await handshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            var shutdown = accessor.Shutdown();
            await accessor.GetPubSubCache();
            Assert.False(shutdown.IsCompleted);
            Assert.False(token.IsCancellationRequested);
            Assert.False(handshake.Task.IsCompleted);
            await receiver.DidNotReceive().Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            handshake.SetResult(null);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(token.IsCancellationRequested);
            Assert.True(data.IsRegistered);
            Assert.True(handshake.Task.IsCompletedSuccessfully);
            Assert.Equal(0, data.PendingHandshakes);
            Assert.Empty(consumer.DeliveredTokens);
            Assert.Equal(0, cache.DeliveryProgressCallCount);
            Assert.Empty(await accessor.GetPubSubCache());
            await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            handshake.TrySetResult(null);
            await accessor.Shutdown().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task Shutdown_DrainsRetirementWithFreshTokenAfterReinitialize()
    {
        var streamId = new QualifiedStreamId("provider", StreamId.Create("retirement", Guid.NewGuid()));
        var token = new EventSequenceTokenV2(1);
        var cache = new ScriptedQueueCache();
        cache.AddToCache([new TestBatchContainer(streamId.StreamId, token)]);
        var pubSub = Substitute.For<IStreamPubSubRuntime>();
        pubSub.RegisterProducer(default, default, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(cache);
        var receiver = Substitute.For<IQueueAdapterReceiver>();
        var agent = CreateAgent(pubSub, QueueId.GetQueueId("queue", 0, 0), receiver, adapterCache);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        var previousToken = default(CancellationToken);

        for (var run = 0; run < 2; run++)
        {
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, token, DateTime.UtcNow);
            var streamData = Assert.Single(await accessor.GetPubSubCache()).Value;
            var subscription = GuidId.GetNewGuidId();
            var data = streamData.AddConsumer(
                subscription, streamId,
                new UnavailableConsumer(Tester.ClientConnectionTests.ClientObserverRoutingTests.CreateUnavailableClientException()),
                null, DateTime.UtcNow);
            data.Cursor = cache.GetCacheCursor(streamId.StreamId, token);
            data.IsRegistered = true;
            var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retirementStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            pubSub.UnregisterConsumerFromProducer(subscription, streamId, agent.GrainId, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    retirementStarted.TrySetResult(call.ArgAt<CancellationToken>(3));
                    return retirement.Task;
                });
            using var callerCancellation = new CancellationTokenSource();
            Task? shutdown = null;
            Task? repeatedShutdown = null;
            var cleanupToken = default(CancellationToken);
            try
            {
                await accessor.RunConsumerCursor(data).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                cleanupToken = await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.True(cleanupToken.CanBeCanceled);
                Assert.False(cleanupToken.IsCancellationRequested);
                Assert.NotEqual(previousToken, cleanupToken);
                shutdown = agent.RunOrQueueTask(() => agent.Shutdown(callerCancellation.Token));
                await accessor.GetPubSubCache();
                callerCancellation.Cancel();
                repeatedShutdown = accessor.Shutdown();
                await accessor.GetPubSubCache();

                Assert.False(shutdown.IsCompleted);
                Assert.False(repeatedShutdown.IsCompleted);
                Assert.False(cleanupToken.IsCancellationRequested);
                await receiver.Received(run).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            }
            finally
            {
                retirement.TrySetResult();
                await Task.WhenAll(shutdown ?? accessor.Shutdown(), repeatedShutdown ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(cleanupToken.IsCancellationRequested);
            await receiver.Received(run + 1).Shutdown(
                Arg.Any<TimeSpan>(), Arg.Is<CancellationToken>(static cancellation => !cancellation.CanBeCanceled));
            previousToken = cleanupToken;
        }
    }
}
