using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PubSubRendezvousGrainTests : OrleansTestingBase, IClassFixture<PubSubRendezvousGrainTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
               builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
            }

            public class SiloHostConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddFaultInjectionMemoryStorage("PubSubStore")
                        .Services.AddSiloStreaming();
                }
            }
        }

        public PubSubRendezvousGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterConsumerFaultTest()
        {
            this.fixture.Logger.LogInformation("************************ RegisterConsumerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            // clean call, to make sure everything is happy and pubsub has state.
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null);
            int consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(1, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when registering a new consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null);
            consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(2, consumers);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnregisterConsumerFaultTest()
        {
            this.fixture.Logger.LogInformation("************************ UnregisterConsumerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            // Add two consumers so when we remove the first it does a storage write, not a storage clear.
            GuidId subscriptionId1 = GuidId.GetGuidId(Guid.NewGuid());
            GuidId subscriptionId2 = GuidId.GetGuidId(Guid.NewGuid());
            await pubSubGrain.RegisterConsumer(subscriptionId1, streamId, default, null);
            await pubSubGrain.RegisterConsumer(subscriptionId2, streamId, default, null);
            int consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(2, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterConsumer(subscriptionId1, streamId));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterConsumer(subscriptionId1, streamId);
            consumers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(1, consumers);

            // inject clear fault, because removing last consumer should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

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
        [Fact(Skip = "This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension"), TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterProducerFaultTest()
        {
            this.fixture.Logger.LogInformation("************************ RegisterProducerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            // clean call, to make sure everything is happy and pubsub has state.
            await pubSubGrain.RegisterProducer(streamId, default);
            int producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(1, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when registering a new producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterProducer(streamId, default));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterProducer(streamId, default);
            producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(2, producers);
        }

        /// <summary>
        /// This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension in the producer management calls.
        /// TODO: Fix rendezvous implementation.
        /// </summary>
        [Fact(Skip = "This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension"), TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnregisterProducerFaultTest()
        {
            this.fixture.Logger.LogInformation("************************ UnregisterProducerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            IStreamProducerExtension firstProducer = new DummyStreamProducerExtension();
            IStreamProducerExtension secondProducer = new DummyStreamProducerExtension();
            // Add two producers so when we remove the first it does a storage write, not a storage clear.
            await pubSubGrain.RegisterProducer(streamId, firstProducer.GetGrainId());
            await pubSubGrain.RegisterProducer(streamId, secondProducer.GetGrainId());
            int producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(2, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, firstProducer.GetGrainId()));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, firstProducer.GetGrainId());
            producers = await pubSubGrain.ProducerCount(streamId);
            Assert.Equal(1, producers);

            // inject clear fault, because removing last producers should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, secondProducer.GetGrainId()));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, secondProducer.GetGrainId());
            producers = await pubSubGrain.ConsumerCount(streamId);
            Assert.Equal(0, producers);
        }

        [Serializable]
        [Orleans.GenerateSerializer]
        public class DummyStreamProducerExtension : IStreamProducerExtension
        {
            [Orleans.Id(0)]
            private readonly Guid id;

            public DummyStreamProducerExtension()
            {
                id = Guid.NewGuid();
            }

            public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string filterData)
            {
                return Task.CompletedTask;
            }

            public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId)
            {
                return Task.CompletedTask;
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
