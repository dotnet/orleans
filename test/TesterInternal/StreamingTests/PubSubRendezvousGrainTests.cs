
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PubSubRendezvousGrainTests : OrleansTestingBase, IClassFixture<PubSubRendezvousGrainTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddFaultyMemoryStorageProvider("PubSubStore");
                return new TestCluster(options);
            }
        }

        public PubSubRendezvousGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterConsumerFaultTest()
        {
            logger.Info("************************ RegisterConsumerFaultTest *********************************");
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), "ProviderName", "StreamNamespace");
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(
                streamId.Guid,
                keyExtension: streamId.ProviderName + "_" + streamId.Namespace);
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(typeof(PubSubRendezvousGrain).FullName);

            // clean call, to make sure everything is happy and pubsub has state.
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, null, null);
            int consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(1, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when registering a new consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, null, null));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, null, null);
            consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(2, consumers);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnregisterConsumerFaultTest()
        {
            logger.Info("************************ UnregisterConsumerFaultTest *********************************");
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), "ProviderName", "StreamNamespace");
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(
                streamId.Guid,
                keyExtension: streamId.ProviderName + "_" + streamId.Namespace);
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(typeof(PubSubRendezvousGrain).FullName);

            // Add two consumers so when we remove the first it does a storage write, not a storage clear.
            GuidId subscriptionId1 = GuidId.GetGuidId(Guid.NewGuid());
            GuidId subscriptionId2 = GuidId.GetGuidId(Guid.NewGuid());
            await pubSubGrain.RegisterConsumer(subscriptionId1, streamId, null, null);
            await pubSubGrain.RegisterConsumer(subscriptionId2, streamId, null, null);
            int consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(2, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterConsumer(subscriptionId1, streamId));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterConsumer(subscriptionId1, streamId);
            consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(1, consumers);

            // inject clear fault, because removing last consumer should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterConsumer(subscriptionId2, streamId));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterConsumer(subscriptionId2, streamId);
            consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(0, consumers);
        }

        /// <summary>
        /// This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension in the producer management calls.
        /// TODO: Fix rendezvous implementation.
        /// </summary>
        /// <returns></returns>
        [Fact(Skip = "This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension"), TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterProducerFaultTest()
        {
            logger.Info("************************ RegisterProducerFaultTest *********************************");
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), "ProviderName", "StreamNamespace");
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(
                streamId.Guid,
                keyExtension: streamId.ProviderName + "_" + streamId.Namespace);
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(typeof(PubSubRendezvousGrain).FullName);

            // clean call, to make sure everything is happy and pubsub has state.
            await pubSubGrain.RegisterProducer(streamId, null);
            int producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(1, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when registering a new producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterProducer(streamId, null));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterProducer(streamId, null);
            producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(2, producers);
        }

        /// <summary>
        /// This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension in the producer management calls.
        /// TODO: Fix rendezvous implementation.
        /// </summary>
        [Fact(Skip = "This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension"), TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnregisterProducerFaultTest()
        {
            logger.Info("************************ UnregisterProducerFaultTest *********************************");
            var streamId = StreamId.GetStreamId(Guid.NewGuid(), "ProviderName", "StreamNamespace");
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(
                streamId.Guid,
                keyExtension: streamId.ProviderName + "_" + streamId.Namespace);
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(typeof(PubSubRendezvousGrain).FullName);

            IStreamProducerExtension firstProducer = new DummyStreamProducerExtension();
            IStreamProducerExtension secondProducer = new DummyStreamProducerExtension();
            // Add two producers so when we remove the first it does a storage write, not a storage clear.
            await pubSubGrain.RegisterProducer(streamId, firstProducer);
            await pubSubGrain.RegisterProducer(streamId, secondProducer);
            int producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(2, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when unregistering a producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, firstProducer));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, firstProducer);
            producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(1, producers);

            // inject clear fault, because removing last producers should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain as GrainReference, new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, secondProducer));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, secondProducer);
            producers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(0, producers);
        }

        [Serializable]
        private class DummyStreamProducerExtension : IStreamProducerExtension
        {
            private readonly Guid id;

            public DummyStreamProducerExtension()
            {
                id = Guid.NewGuid();
            }

            public Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer,
                IStreamFilterPredicateWrapper filter)
            {
                return TaskDone.Done;
            }

            public Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId)
            {
                return TaskDone.Done;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != GetType()) return false;
                return Equals((DummyStreamProducerExtension)obj);
            }

            public override int GetHashCode()
            {
                return id.GetHashCode();
            }

            private bool Equals(DummyStreamProducerExtension other)
            {
                return id.Equals(other.id);
            }
        }
    }
}
