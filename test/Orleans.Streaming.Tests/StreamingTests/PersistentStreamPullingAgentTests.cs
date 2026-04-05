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
        public async Task ReadFromQueue_WaitsForColdStreamRegistration()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(_ => registration.Task);

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(1)
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                ]));

            var agent = CreateAgent(pubSub, queueId);

            var readTask = InvokeReadFromQueue(agent, queueId, receiver, 1);

            Assert.False(readTask.IsCompleted);

            registration.SetResult(new HashSet<PubSubSubscriptionState>());

            Assert.True(await readTask);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_RemovesCacheEntryWhenProducerRegistrationTerminates()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub: null, queueId);

            await Assert.ThrowsAsync<NullReferenceException>(() => InvokeRegisterStream(agent, streamId, new EventSequenceTokenV2(1), DateTime.UtcNow));

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

            return new PersistentStreamPullingAgent(
                SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("persistent-stream-pulling-agent-test"), siloAddress),
                "provider",
                pubSub!,
                new NoOpStreamFilter(),
                queueId,
                new StreamPullingAgentOptions(),
                Substitute.For<IQueueAdapter>(),
                queueAdapterCache: null,
                new NoOpStreamDeliveryFailureHandler(),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                TimeProvider.System,
                shared);
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
            return (Task)RegisterStreamMethod.Invoke(agent, [streamId, firstToken, now])!;
        }
    }
}
