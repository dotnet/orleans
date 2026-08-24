using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Placement;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
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
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null!, TestContext.Current.CancellationToken);
            int consumers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(1, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when registering a new consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null!, TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, default, null!, TestContext.Current.CancellationToken);
            consumers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
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
            await pubSubGrain.RegisterConsumer(subscriptionId1, streamId, default, null!, TestContext.Current.CancellationToken);
            await pubSubGrain.RegisterConsumer(subscriptionId2, streamId, default, null!, TestContext.Current.CancellationToken);
            int consumers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(2, consumers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterConsumer(subscriptionId1, streamId, TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterConsumer(subscriptionId1, streamId, TestContext.Current.CancellationToken);
            consumers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(1, consumers);

            // inject clear fault, because removing last consumer should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterConsumer(subscriptionId2, streamId, TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterConsumer(subscriptionId2, streamId, TestContext.Current.CancellationToken);
            consumers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(0, consumers);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterProducer_RemovesPersistedPullingAgentFromDefunctSilo()
        {
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var primarySilo = this.fixture.HostedCluster.Primary!;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, primarySilo.SiloAddress);
            try
            {
                Assert.Equal(0, await pubSubGrain.ProducerCount(streamId));
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var managementGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var rendezvousSilo = await managementGrain.GetActivationAddress(pubSubGrain);
            Assert.Equal(primarySilo.SiloAddress, rendezvousSilo);
            var restartedSilo = this.fixture.HostedCluster.GetActiveSilos().First(silo => silo.SiloAddress != rendezvousSilo);
            var staleProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                restartedSilo.SiloAddress,
                "ProviderName_1_test-queue").GrainId;

            await pubSubGrain.RegisterProducer(streamId, staleProducer, new MembershipVersion(1));
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId));

            var replacementSilo = await this.fixture.HostedCluster.RestartSiloAsync(restartedSilo);
            Assert.NotNull(replacementSilo);
            await this.fixture.HostedCluster.WaitForLivenessToStabilizeAsync();
            var replacementProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                replacementSilo.SiloAddress,
                "ProviderName_1_test-queue").GrainId;

            await pubSubGrain.RegisterProducer(streamId, replacementProducer, new MembershipVersion(1));

            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId));
            await managementGrain.ForceActivationCollection(TimeSpan.Zero);
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId));
            await pubSubGrain.UnregisterProducer(streamId, replacementProducer);
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId));
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterProducer_DoesNotRestorePullingAgentFromDefunctSilo()
        {
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId));
            var managementGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var activeSilo = await managementGrain.GetActivationAddress(pubSubGrain);
            Assert.NotNull(activeSilo);
            var defunctSilo = SiloAddress.New(activeSilo.Endpoint, activeSilo.Generation - 1);
            var defunctProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                defunctSilo,
                "ProviderName_1_test-queue").GrainId;
            var replacementProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                activeSilo,
                "ProviderName_1_test-queue").GrainId;

            await pubSubGrain.RegisterProducer(streamId, replacementProducer);
            await Assert.ThrowsAsync<OrleansException>(() => pubSubGrain.RegisterProducer(streamId, defunctProducer));

            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId));
            await managementGrain.ForceActivationCollection(TimeSpan.Zero);
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId));
            await pubSubGrain.UnregisterProducer(streamId, replacementProducer);
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId));
        }

        [Theory]
        [InlineData(SiloStatus.None, false)]
        [InlineData(SiloStatus.Active, true)]
        [InlineData(SiloStatus.ShuttingDown, false)]
        [InlineData(SiloStatus.Stopping, false)]
        [InlineData(SiloStatus.Dead, false)]
        [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void SystemTargetRegistrationRequiresKnownNonTerminatingSilo(SiloStatus status, bool expected) =>
            Assert.Equal(expected, PubSubRendezvousGrain.IsValidSystemTargetRegistrationStatus(status));

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void DefaultMembershipVersionIdentifiesLegacyPublisherState()
        {
            Assert.Equal(default, PubSubPublisherState.UnknownMembershipVersion);
            Assert.False(PubSubRendezvousGrain.HasMembershipVersion(default));
            Assert.True(PubSubRendezvousGrain.HasMembershipVersion(new MembershipVersion(1)));
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnversionedReplacementRecomputesVersionedPublisherFromPostRefreshSnapshot()
        {
            var staleSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var replacementSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 2);
            var staleSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [staleSilo] = new(staleSilo, SiloStatus.Active, "stale"),
                }.ToImmutableDictionary(),
                new MembershipVersion(1));
            var freshSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [staleSilo] = new(staleSilo, SiloStatus.Dead, "stale"),
                    [replacementSilo] = new(replacementSilo, SiloStatus.Active, "replacement"),
                }.ToImmutableDictionary(),
                new MembershipVersion(2));
            var versions = new Dictionary<SiloAddress, MembershipVersion>
            {
                [staleSilo] = new(1),
                [replacementSilo] = PubSubPublisherState.UnknownMembershipVersion,
            };

            var statuses = await PubSubRendezvousGrain.GetSiloStatuses(
                staleSnapshot,
                versions,
                (snapshot, unversionedSilos, cancellationToken) =>
                {
                    Assert.Same(staleSnapshot, snapshot);
                    Assert.Equal([replacementSilo], unversionedSilos);
                    Assert.Equal(CancellationToken.None, cancellationToken);
                    return ValueTask.FromResult(
                        new UnknownSiloStatusCache.SiloStatusValidationResult(
                            new Dictionary<SiloAddress, SiloStatus>
                            {
                                [replacementSilo] = SiloStatus.Active,
                            },
                            freshSnapshot));
                },
                CancellationToken.None);

            Assert.Equal(SiloStatus.Dead, statuses[staleSilo]);
            Assert.Equal(SiloStatus.Active, statuses[replacementSilo]);
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
            await pubSubGrain.RegisterProducer(streamId, default, TestContext.Current.CancellationToken);
            int producers = await pubSubGrain.ProducerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(1, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when registering a new producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterProducer(streamId, default, TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterProducer(streamId, default, TestContext.Current.CancellationToken);
            producers = await pubSubGrain.ProducerCount(streamId, TestContext.Current.CancellationToken);
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
            await pubSubGrain.RegisterProducer(streamId, firstProducer.GetGrainId(), TestContext.Current.CancellationToken);
            await pubSubGrain.RegisterProducer(streamId, secondProducer.GetGrainId(), TestContext.Current.CancellationToken);
            int producers = await pubSubGrain.ProducerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(2, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, firstProducer.GetGrainId(), TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, firstProducer.GetGrainId(), TestContext.Current.CancellationToken);
            producers = await pubSubGrain.ProducerCount(streamId, TestContext.Current.CancellationToken);
            Assert.Equal(1, producers);

            // inject clear fault, because removing last producers should trigger a clear storage call.
            await faultGrain.AddFaultOnClear(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when unregistering a consumer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.UnregisterProducer(streamId, secondProducer.GetGrainId(), TestContext.Current.CancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.UnregisterProducer(streamId, secondProducer.GetGrainId(), TestContext.Current.CancellationToken);
            producers = await pubSubGrain.ConsumerCount(streamId, TestContext.Current.CancellationToken);
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

            public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override bool Equals(object? obj)
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
