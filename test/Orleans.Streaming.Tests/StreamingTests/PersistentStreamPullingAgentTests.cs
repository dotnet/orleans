using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.Timers;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PersistentStreamPullingAgentTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_DoesNotWaitForColdStreamRegistration()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(_ => registration.Task);

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            // Use Arg.Any<int>() to match regardless of the maxCacheAddCount value.
            receiver.GetQueueMessagesAsync(Arg.Any<int>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                ]));

            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            var readTask = testAccessor.ReadFromQueue(queueId, receiver, 1);

            // ReadFromQueue adds the stream entry synchronously and tracks the in-flight
            // background registration task for the cold stream.
            var cache = await testAccessor.GetPubSubCache();
            Assert.Single(cache);

            var (_, streamData) = cache.Single();
            var registrationTask = streamData.RegistrationTask;
            Assert.NotNull(registrationTask);
            Assert.False(registrationTask.IsCompleted, "Registration should still be in progress");

            Assert.True(await readTask, "ReadFromQueue should return true indicating data was read");

            // Completing registration should resolve the tracked task and clear it.
            registration.SetResult(new HashSet<PubSubSubscriptionState>());
            await registrationTask;
            Assert.Null(streamData.RegistrationTask);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_ClearsRegistrationTaskWhenColdStreamRegistrationCompletesSynchronously()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                ]));

            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            var readResult = await testAccessor.ReadFromQueue(queueId, receiver, 1);
            Assert.True(readResult, "ReadFromQueue should return true indicating data was read");

            var cache = await testAccessor.GetPubSubCache();
            Assert.Single(cache);

            var (_, streamData) = cache.Single();
            var registrationTask = streamData.RegistrationTask;
            if (registrationTask is not null)
            {
                await registrationTask;
                Assert.Null(streamData.RegistrationTask);
            }

            Assert.True(streamData.StreamRegistered);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_DoesNotStartQueueReadAfterShutdownStarts()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await testAccessor.Shutdown();

            var readResult = await testAccessor.ReadFromQueue(queueId, receiver, 1);

            Assert.False(readResult);
            Assert.Empty(receiver.ReceivedCalls());
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_RemovesCacheEntryWhenProducerRegistrationTerminates()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            Assert.Empty(await testAccessor.GetPubSubCache());
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_DoesNotRegisterProducerAfterShutdownStarts()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await testAccessor.Shutdown();
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            Assert.Empty(await testAccessor.GetPubSubCache());
            Assert.Empty(pubSub.ReceivedCalls());
        }

        private static PersistentStreamPullingAgent CreateAgent(IStreamPubSub pubSub, QueueId queueId, IQueueAdapterReceiver receiver = null)
        {
            var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(siloAddress);
            var timerRegistry = Substitute.For<ITimerRegistry>();
            timerRegistry.RegisterGrainTimer(
                    Arg.Any<IGrainContext>(),
                    Arg.Any<Func<QueueId, CancellationToken, Task>>(),
                    Arg.Any<QueueId>(),
                    Arg.Any<GrainTimerCreationOptions>())
                .Returns(Substitute.For<IGrainTimer>());

            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry,
                activations: new ActivationDirectory());

            receiver ??= Substitute.For<IQueueAdapterReceiver>();
            receiver.Initialize(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueAdapter = Substitute.For<IQueueAdapter>();
            queueAdapter.Name.Returns("provider");
            queueAdapter.CreateReceiver(Arg.Any<QueueId>()).Returns(receiver);

            return new PersistentStreamPullingAgent(
                SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("persistent-stream-pulling-agent-test"), siloAddress),
                "provider",
                pubSub!,
                new NoOpStreamFilter(),
                queueId,
                new StreamPullingAgentOptions(),
                queueAdapter,
                queueAdapterCache: null,
                new NoOpStreamDeliveryFailureHandler(),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                TimeProvider.System,
                shared);
        }

        private static Task InitializeAgent(PersistentStreamPullingAgent agent) => agent.RunOrQueueTask(() => agent.Initialize());

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_KeepsCacheEntryWhenSubscriberHandshakeFails()
        {
            // A subscriber whose grain reference cannot be resolved (RuntimeClient is null in test setup)
            // simulates a handshake failure.  The stream entry must survive.
            var subscriptionId = GuidId.GetGuidId(Guid.NewGuid());
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var consumerGrainId = GrainId.Create("test", Guid.NewGuid().ToString());

            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(
                    new HashSet<PubSubSubscriptionState>
                    {
                        new PubSubSubscriptionState(subscriptionId, streamId, consumerGrainId),
                    }));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            // RegisterStream should complete without throwing even though the subscriber
            // handshake will fault (NullReferenceException from the null RuntimeClient).
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var cache = await testAccessor.GetPubSubCache();
            Assert.True(cache.ContainsKey(streamId), "Stream entry must remain in pubsub cache after a subscriber-handshake failure.");
            Assert.True(cache[streamId].StreamRegistered, "StreamRegistered must be true once producer registration succeeds.");
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_WaitsForInFlightPumpWork()
        {
            var queueReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queueReadReleased = new TaskCompletionSource<IList<IBatchContainer>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>())
                .Returns(async _ =>
                {
                    queueReadStarted.TrySetResult(true);
                    return await queueReadReleased.Task;
                });
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await InitializeAgent(agent);

            var pumpTask = testAccessor.RunQueuePump(queueId, CancellationToken.None);
            await queueReadStarted.Task;

            var shutdownTask = testAccessor.Shutdown();
            Assert.False(shutdownTask.IsCompleted);

            queueReadReleased.SetResult(new List<IBatchContainer>());

            await shutdownTask;
            await pumpTask;
        }
    }
}
