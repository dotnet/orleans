using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PersistentStreamPullingAgentTests
    {
        private static readonly MethodInfo ReadFromQueueMethod = typeof(PersistentStreamPullingAgent).GetMethod("ReadFromQueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo RegisterStreamMethod = typeof(PersistentStreamPullingAgent).GetMethod("RegisterStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo PubSubCacheField = typeof(PersistentStreamPullingAgent).GetField("pubSubCache", BindingFlags.Instance | BindingFlags.NonPublic)!;

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

            var readTask = InvokeReadFromQueue(agent, queueId, receiver, 1);

            // ReadFromQueue should complete immediately without waiting for cold-stream registration.
            Assert.True(readTask.IsCompletedSuccessfully, $"ReadFromQueue should have completed synchronously (IsFaulted={readTask.IsFaulted})");

            // ReadFromQueue adds the stream entry synchronously and tracks the in-flight
            // background registration task for the cold stream.
            var cache = GetPubSubCache(agent);
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

            var readTask = InvokeReadFromQueue(agent, queueId, receiver, 1);

            Assert.True(readTask.IsCompletedSuccessfully, $"ReadFromQueue should have completed synchronously (IsFaulted={readTask.IsFaulted})");
            Assert.True(await readTask, "ReadFromQueue should return true indicating data was read");

            var cache = GetPubSubCache(agent);
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
        public async Task RegisterStream_RemovesCacheEntryWhenProducerRegistrationTerminates()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub: null, queueId);

            await InvokeRegisterStream(agent, streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            Assert.Empty(GetPubSubCache(agent));
        }

        private static PersistentStreamPullingAgent CreateAgent(IStreamPubSub pubSub, QueueId queueId)
        {
            var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(siloAddress);

            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory());

            var queueAdapter = Substitute.For<IQueueAdapter>();
            queueAdapter.Name.Returns("provider");

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

            // RegisterStream should complete without throwing even though the subscriber
            // handshake will fault (NullReferenceException from the null RuntimeClient).
            await InvokeRegisterStream(agent, streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var cache = GetPubSubCache(agent);
            Assert.True(cache.ContainsKey(streamId), "Stream entry must remain in pubsub cache after a subscriber-handshake failure.");
            Assert.True(cache[streamId].StreamRegistered, "StreamRegistered must be true once producer registration succeeds.");
        }

        private static Dictionary<QualifiedStreamId, StreamConsumerCollection> GetPubSubCache(PersistentStreamPullingAgent agent)
        {
            return (Dictionary<QualifiedStreamId, StreamConsumerCollection>)PubSubCacheField.GetValue(agent)!;
        }

        private static Task<bool> InvokeReadFromQueue(PersistentStreamPullingAgent agent, QueueId queueId, IQueueAdapterReceiver receiver, int maxCacheAddCount)
        {
            return (Task<bool>)ReadFromQueueMethod.Invoke(agent, [queueId, receiver, maxCacheAddCount])!;
        }

        private static Task InvokeRegisterStream(PersistentStreamPullingAgent agent, QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now)
        {
            RegisterStreamMethod.Invoke(agent, [streamId, firstToken, now]);

            if (GetPubSubCache(agent).TryGetValue(streamId, out var streamData) && streamData.RegistrationTask is { } registrationTask)
            {
                return registrationTask;
            }

            return Task.CompletedTask;
        }
    }
}
