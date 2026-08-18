using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    /// <summary>
    /// Tests for stream subscription behavior with stateless worker grains, verifying subscription restrictions.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Streaming")]
    public class StatelessWorkersStreamTests : OrleansTestingBase, IClassFixture<StatelessWorkersStreamTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
            }

            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(
                            StreamProvider,
                            streams => streams.ConfigurePartitioning(PartitionCount))
                        .AddMemoryGrainStorage("PubSubStore");
                    hostBuilder.Services.AddSingleton<StatelessWorkerStreamConsumerState>();
                }
            }

            public class ClientConfiguretor : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(
                        StreamProvider,
                        streams => streams.ConfigurePartitioning(PartitionCount));
                }
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private const int PartitionCount = 4;
        private const string StreamProvider = StreamTestsConstants.MEMORY_STREAM_PROVIDER_NAME;

        public StatelessWorkersStreamTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional")]
        public async Task ExplicitSubscription_DeliversToStatelessWorker_AndCanBeRemovedAtGrainScope()
        {
            var state = GetConsumerState();
            state.Reset(expectedDeliveries: 1);
            var streamId = Guid.NewGuid();
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(0);

            await consumer.BecomeConsumer([streamId], StreamProvider);
            await GetStream(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId).OnNextAsync("first");
            await state.WaitForDeliveriesAsync(Timeout);

            Assert.Equal(1, state.DeliveryCount);
            Assert.Equal(1, await consumer.StopConsuming(streamId, StreamProvider));
            Assert.Equal(0, await consumer.StopConsuming(streamId, StreamProvider));
        }

        [Fact, TestCategory("Functional")]
        public async Task ImplicitSubscription_AttachesObserverBeforeFirstDelivery()
        {
            var state = GetConsumerState();
            state.Reset(expectedDeliveries: 1);
            var streamId = Guid.NewGuid();

            await GetStream(ImplicitStatelessWorkerStreamConsumerGrain.StreamNamespace, streamId).OnNextAsync("first");
            await state.WaitForDeliveriesAsync(Timeout);

            Assert.Equal(1, state.DeliveryCount);
            Assert.Equal(1, state.ObserverActivationCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task ConcurrentQueueDeliveries_UseMultipleLocalWorkerActivations()
        {
            var state = GetConsumerState();
            state.Reset(expectedDeliveries: PartitionCount, blockDeliveries: true);
            var streamIds = CreateStreamIdsForDistinctQueues();
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(1);
            await consumer.BecomeConsumer(streamIds, StreamProvider);

            await Task.WhenAll(streamIds.Select((streamId, index) =>
                GetStream(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId)
                    .OnNextAsync($"item-{index}")));
            await state.WaitForBlockedDeliveriesAsync(Timeout);

            Assert.Equal(PartitionCount, state.WaitingDeliveryCount);
            Assert.Equal(PartitionCount, state.DeliveryActivationCount);
            Assert.Equal(PartitionCount, state.ObserverActivationCount);

            state.ReleaseDeliveries(PartitionCount);
            await state.WaitForReleasedDeliveriesAsync(Timeout);
            Assert.Equal(PartitionCount, state.DeliveryCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task SubscribeToStream_FromStatelessWorkerWithoutSubscriptionObserver_Fails()
        {
            var consumer = fixture.GrainFactory.GetGrain<IUnsupportedStatelessWorkerStreamConsumerGrain>(0);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => consumer.BecomeConsumer(Guid.NewGuid(), StreamProvider));

            Assert.Contains(typeof(Orleans.Streams.Core.IStreamSubscriptionObserver).FullName!, exception.Message);
        }

        [Fact, TestCategory("Functional")]
        public async Task SubscribeToStream_FromStatelessWorkerWithSequenceToken_Fails()
        {
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(2);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => consumer.BecomeConsumerFromToken(Guid.NewGuid(), StreamProvider));

            Assert.Contains("null sequence token", exception.Message);
        }

        private StatelessWorkerStreamConsumerState GetConsumerState() =>
            fixture.HostedCluster.GetSiloServiceProvider().GetRequiredService<StatelessWorkerStreamConsumerState>();

        private IAsyncStream<string> GetStream(string streamNamespace, Guid streamId) =>
            fixture.Client.GetStreamProvider(StreamProvider).GetStream<string>(streamNamespace, streamId);

        private static Guid[] CreateStreamIdsForDistinctQueues()
        {
            var mapper = new HashRingBasedStreamQueueMapper(
                new HashRingStreamQueueMapperOptions { TotalQueueCount = PartitionCount },
                StreamProvider);
            var result = new Dictionary<QueueId, Guid>();
            while (result.Count < PartitionCount)
            {
                var streamId = Guid.NewGuid();
                var queueId = mapper.GetQueueForStream(
                    StreamId.Create(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId));
                result.TryAdd(queueId, streamId);
            }

            return result.Values.ToArray();
        }
    }
}