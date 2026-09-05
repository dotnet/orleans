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
            var cancellationToken = TestContext.Current.CancellationToken;
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var primarySilo = this.fixture.HostedCluster.Primary!;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, primarySilo.SiloAddress);
            try
            {
                Assert.Equal(0, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var managementGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var rendezvousSilo = await managementGrain.GetActivationAddress(pubSubGrain, cancellationToken);
            Assert.Equal(primarySilo.SiloAddress, rendezvousSilo);
            var restartedSilo = this.fixture.HostedCluster.GetActiveSilos().First(silo => silo.SiloAddress != rendezvousSilo);
            var staleProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                restartedSilo.SiloAddress,
                "ProviderName_1_test-queue").GrainId;

            await pubSubGrain.RegisterProducer(streamId, staleProducer, new MembershipVersion(1), cancellationToken);
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId, cancellationToken));

            var replacementSilo = await this.fixture.HostedCluster.RestartSiloAsync(restartedSilo);
            Assert.NotNull(replacementSilo);
            await this.fixture.HostedCluster.WaitForLivenessToStabilizeAsync();
            var replacementProducer = SystemTargetGrainId.Create(
                Constants.StreamPullingAgentType,
                replacementSilo.SiloAddress,
                "ProviderName_1_test-queue").GrainId;

            await pubSubGrain.RegisterProducer(streamId, replacementProducer, new MembershipVersion(1), cancellationToken);

            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            await managementGrain.ForceActivationCollection(TimeSpan.Zero, cancellationToken);
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            await pubSubGrain.UnregisterProducer(streamId, replacementProducer, cancellationToken);
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId, cancellationToken));
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task RegisterProducer_DoesNotRestorePullingAgentFromDefunctSilo()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            var managementGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var activeSilo = await managementGrain.GetActivationAddress(pubSubGrain, cancellationToken);
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

            await pubSubGrain.RegisterProducer(streamId, replacementProducer, cancellationToken: cancellationToken);
            await Assert.ThrowsAsync<OrleansException>(
                () => pubSubGrain.RegisterProducer(streamId, defunctProducer, cancellationToken: cancellationToken));

            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            await managementGrain.ForceActivationCollection(TimeSpan.Zero, cancellationToken);
            Assert.Equal(1, await pubSubGrain.ProducerCount(streamId, cancellationToken));
            await pubSubGrain.UnregisterProducer(streamId, replacementProducer, cancellationToken);
            Assert.Equal(0, await pubSubGrain.ProducerCount(streamId, cancellationToken));
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

        [Theory]
        [InlineData(SiloStatus.None, false)]
        [InlineData(SiloStatus.Active, true)]
        [InlineData(SiloStatus.Dead, true)]
        [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void ReplacementRegistrationRequiresExistingPublisherStatus(
            SiloStatus existingPublisherStatus,
            bool expected)
        {
            var existingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var replacementSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 2);
            var statuses = new Dictionary<SiloAddress, SiloStatus>
            {
                [existingSilo] = existingPublisherStatus,
                [replacementSilo] = SiloStatus.Active,
            };

            var actual = PubSubRendezvousGrain.ShouldRegisterSystemTarget(
                replacementSilo,
                [existingSilo],
                statuses);

            Assert.Equal(expected, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void DefaultMembershipVersionIdentifiesLegacyPublisherState()
        {
            Assert.Equal(default, PubSubPublisherState.UnknownMembershipVersion);
            Assert.False(PubSubRendezvousGrain.HasMembershipVersion(default));
            Assert.True(PubSubRendezvousGrain.HasMembershipVersion(new MembershipVersion(1)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void AnyLegacyPublisherRequiresFreshValidation(bool addLegacyFirst)
        {
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var versions = new Dictionary<SiloAddress, MembershipVersion>();
            var knownVersion = new MembershipVersion(10);
            var firstVersion = addLegacyFirst ? PubSubPublisherState.UnknownMembershipVersion : knownVersion;
            var secondVersion = addLegacyFirst ? knownVersion : PubSubPublisherState.UnknownMembershipVersion;

            PubSubRendezvousGrain.AddSiloMembershipVersion(versions, silo, firstVersion);
            PubSubRendezvousGrain.AddSiloMembershipVersion(versions, silo, secondVersion);

            Assert.Equal(PubSubPublisherState.UnknownMembershipVersion, versions[silo]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnversionedReplacementRequiresFreshValidationAndRecomputesVersionedPublisher()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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

            var validation = await PubSubRendezvousGrain.GetSiloStatuses(
                staleSnapshot,
                versions,
                (snapshot, unversionedSilos, cancellationToken, requireFresh) =>
                {
                    Assert.Same(staleSnapshot, snapshot);
                    Assert.Equal([replacementSilo], unversionedSilos);
                    Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                    Assert.True(requireFresh);
                    return ValueTask.FromResult(
                        new UnknownSiloStatusCache.SiloStatusValidationResult(
                            new Dictionary<SiloAddress, SiloStatus>
                            {
                                [replacementSilo] = SiloStatus.Active,
                            },
                            freshSnapshot));
                },
                cancellationToken);

            var statuses = validation.Statuses;
            Assert.Same(freshSnapshot, validation.Snapshot);
            Assert.Equal(SiloStatus.Dead, statuses[staleSilo]);
            Assert.Equal(SiloStatus.Active, statuses[replacementSilo]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task NewerCachedDeadStatusIsNotOverwrittenByOlderActiveRefreshSnapshot()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var initialSnapshot = new ClusterMembershipSnapshot(
                ImmutableDictionary<SiloAddress, ClusterMember>.Empty,
                new MembershipVersion(9));
            var olderRefreshSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [silo] = new(silo, SiloStatus.Active, "stale"),
                }.ToImmutableDictionary(),
                new MembershipVersion(10));
            var versions = new Dictionary<SiloAddress, MembershipVersion>
            {
                [silo] = new(10),
            };

            var validation = await PubSubRendezvousGrain.GetSiloStatuses(
                initialSnapshot,
                versions,
                (snapshot, silosRequiringFreshValidation, actualCancellationToken, requireFresh) =>
                {
                    Assert.Same(initialSnapshot, snapshot);
                    Assert.Equal([silo], silosRequiringFreshValidation);
                    Assert.Equal(cancellationToken, actualCancellationToken);
                    Assert.True(requireFresh);
                    return ValueTask.FromResult(
                        new UnknownSiloStatusCache.SiloStatusValidationResult(
                            new Dictionary<SiloAddress, SiloStatus>
                            {
                                [silo] = SiloStatus.Dead,
                            },
                            olderRefreshSnapshot));
                },
                cancellationToken);

            Assert.Same(olderRefreshSnapshot, validation.Snapshot);
            Assert.Equal(SiloStatus.Dead, validation.Statuses[silo]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void FreshSnapshotBackfillsLegacyPublisherMembershipVersion()
        {
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var snapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [silo] = new(silo, SiloStatus.Active, "silo"),
                }.ToImmutableDictionary(),
                new MembershipVersion(10));

            var actual = PubSubRendezvousGrain.GetValidatedMembershipVersion(
                PubSubPublisherState.UnknownMembershipVersion,
                silo,
                SiloStatus.Active,
                snapshot);

            Assert.Equal(snapshot.Version, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public void OlderSnapshotDoesNotBackfillNewerValidatedStatus()
        {
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var snapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [silo] = new(silo, SiloStatus.Active, "stale"),
                }.ToImmutableDictionary(),
                new MembershipVersion(10));

            var actual = PubSubRendezvousGrain.GetValidatedMembershipVersion(
                PubSubPublisherState.UnknownMembershipVersion,
                silo,
                SiloStatus.Dead,
                snapshot);

            Assert.Equal(PubSubPublisherState.UnknownMembershipVersion, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PersistedFuturePublisherVersionUsesFreshValidation()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var staleSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var replacementSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 2);
            var staleSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [replacementSilo] = new(replacementSilo, SiloStatus.Active, "replacement"),
                }.ToImmutableDictionary(),
                new MembershipVersion(1));
            var refreshedSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [staleSilo] = new(staleSilo, SiloStatus.Dead, "stale"),
                    [replacementSilo] = new(replacementSilo, SiloStatus.Active, "replacement"),
                }.ToImmutableDictionary(),
                new MembershipVersion(2));
            var versions = new Dictionary<SiloAddress, MembershipVersion>
            {
                [staleSilo] = new(42),
                [replacementSilo] = new(1),
            };

            var validation = await PubSubRendezvousGrain.GetSiloStatuses(
                staleSnapshot,
                versions,
                (snapshot, silosRequiringFreshValidation, actualCancellationToken, requireFresh) =>
                {
                    Assert.Same(staleSnapshot, snapshot);
                    Assert.Equal([staleSilo], silosRequiringFreshValidation);
                    Assert.Equal(cancellationToken, actualCancellationToken);
                    Assert.True(requireFresh);
                    return ValueTask.FromResult(
                        new UnknownSiloStatusCache.SiloStatusValidationResult(
                            new Dictionary<SiloAddress, SiloStatus>
                            {
                                [staleSilo] = SiloStatus.Dead,
                            },
                            refreshedSnapshot));
                },
                cancellationToken);

            var statuses = validation.Statuses;
            Assert.Equal(SiloStatus.Dead, statuses[staleSilo]);
            Assert.Equal(SiloStatus.Active, statuses[replacementSilo]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task FutureReplacementVersionUsesFreshValidation()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var staleSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var replacementSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 2);
            var staleSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [staleSilo] = new(staleSilo, SiloStatus.Active, "stale"),
                }.ToImmutableDictionary(),
                new MembershipVersion(1));
            var refreshedSnapshot = new ClusterMembershipSnapshot(
                new Dictionary<SiloAddress, ClusterMember>
                {
                    [staleSilo] = new(staleSilo, SiloStatus.Dead, "stale"),
                    [replacementSilo] = new(replacementSilo, SiloStatus.Active, "replacement"),
                }.ToImmutableDictionary(),
                new MembershipVersion(2));
            var versions = new Dictionary<SiloAddress, MembershipVersion>
            {
                [staleSilo] = new(1),
                [replacementSilo] = new(2),
            };

            var validation = await PubSubRendezvousGrain.GetSiloStatuses(
                staleSnapshot,
                versions,
                (snapshot, silosRequiringFreshValidation, actualCancellationToken, requireFresh) =>
                {
                    Assert.Same(staleSnapshot, snapshot);
                    Assert.Equal([replacementSilo], silosRequiringFreshValidation);
                    Assert.Equal(cancellationToken, actualCancellationToken);
                    Assert.True(requireFresh);
                    return ValueTask.FromResult(
                        new UnknownSiloStatusCache.SiloStatusValidationResult(
                            new Dictionary<SiloAddress, SiloStatus>
                            {
                                [replacementSilo] = SiloStatus.Active,
                            },
                            refreshedSnapshot));
                },
                cancellationToken);

            var statuses = validation.Statuses;
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
            var cancellationToken = TestContext.Current.CancellationToken;
            this.fixture.Logger.LogInformation("************************ RegisterProducerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            // clean call, to make sure everything is happy and pubsub has state.
            await pubSubGrain.RegisterProducer(streamId, default, cancellationToken: cancellationToken);
            int producers = await pubSubGrain.ProducerCount(streamId, cancellationToken);
            Assert.Equal(1, producers);

            // inject fault
            await faultGrain.AddFaultOnWrite(pubSubGrain.GetGrainId(), new ApplicationException("Write"));

            // expect exception when registering a new producer
            await Assert.ThrowsAsync<OrleansException>(
                    () => pubSubGrain.RegisterProducer(streamId, default, cancellationToken: cancellationToken));

            // pubsub grain should recover and still function
            await pubSubGrain.RegisterProducer(streamId, default, cancellationToken: cancellationToken);
            producers = await pubSubGrain.ProducerCount(streamId, cancellationToken);
            Assert.Equal(2, producers);
        }

        /// <summary>
        /// This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension in the producer management calls.
        /// TODO: Fix rendezvous implementation.
        /// </summary>
        [Fact(Skip = "This test fails because the producer must be grain reference which is not implied by the IStreamProducerExtension"), TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task UnregisterProducerFaultTest()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            this.fixture.Logger.LogInformation("************************ UnregisterProducerFaultTest *********************************");
            var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
            var pubSubGrain = this.fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
            var faultGrain = this.fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));

            IStreamProducerExtension firstProducer = new DummyStreamProducerExtension();
            IStreamProducerExtension secondProducer = new DummyStreamProducerExtension();
            // Add two producers so when we remove the first it does a storage write, not a storage clear.
            await pubSubGrain.RegisterProducer(
                streamId,
                firstProducer.GetGrainId(),
                cancellationToken: cancellationToken);
            await pubSubGrain.RegisterProducer(
                streamId,
                secondProducer.GetGrainId(),
                cancellationToken: cancellationToken);
            int producers = await pubSubGrain.ProducerCount(streamId, cancellationToken);
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
