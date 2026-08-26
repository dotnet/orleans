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
                    hostBuilder.Services.AddSingleton<StreamingDiagnosticEventRecorder>();
                    hostBuilder.AddStartupTask<StreamingDiagnosticEventRecorder>(ServiceLifecycleStage.RuntimeInitialize);
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
            await WaitForStreamingProviderReadyAsync();
            var state = GetConsumerState();
            var deliveryId = Guid.NewGuid().ToString();
            var run = state.StartRun(deliveryId, expectedDeliveries: 1);
            var streamId = Guid.NewGuid();
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(0);

            await consumer.BecomeConsumer([streamId], StreamProvider);
            await GetStream(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId).OnNextAsync(deliveryId);
            await run.WaitForDeliveriesAsync(Timeout);

            Assert.Equal(1, run.DeliveryCount);
            Assert.Equal(1, await consumer.StopConsuming(streamId, StreamProvider));
            Assert.Equal(0, await consumer.StopConsuming(streamId, StreamProvider));
        }

        [Fact, TestCategory("Functional")]
        public async Task ImplicitSubscription_AttachesObserverBeforeFirstDelivery()
        {
            await WaitForStreamingProviderReadyAsync();
            var state = GetConsumerState();
            var deliveryId = Guid.NewGuid().ToString();
            var run = state.StartRun(deliveryId, expectedDeliveries: 1);
            var streamId = Guid.NewGuid();

            await GetStream(ImplicitStatelessWorkerStreamConsumerGrain.StreamNamespace, streamId).OnNextAsync(deliveryId);
            await run.WaitForDeliveriesAsync(Timeout);

            Assert.Equal(1, run.DeliveryCount);
            Assert.Equal(1, run.ObserverActivationCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task ConcurrentQueueDeliveries_UseMultipleLocalWorkerActivations()
        {
            await WaitForStreamingProviderReadyAsync();
            var state = GetConsumerState();
            var deliveryId = Guid.NewGuid().ToString();
            var run = state.StartRun(deliveryId, expectedDeliveries: PartitionCount, blockDeliveries: true);
            var streamIds = CreateStreamIdsForDistinctQueues();
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(1);
            await consumer.BecomeConsumer(streamIds, StreamProvider);

            try
            {
                await Task.WhenAll(streamIds.Select(streamId =>
                    GetStream(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId)
                        .OnNextAsync(deliveryId)));
                await run.WaitForDeliveriesAsync(Timeout);

                Assert.Equal(PartitionCount, run.WaitingDeliveryCount);
                Assert.Equal(PartitionCount, run.DeliveryActivationCount);
                Assert.Equal(PartitionCount, run.ObserverActivationCount);
            }
            finally
            {
                await run.ReleaseDeliveriesAsync(Timeout);
                await Task.WhenAll(streamIds.Select(streamId =>
                    consumer.StopConsuming(streamId, StreamProvider)));
            }

            Assert.Equal(PartitionCount, run.DeliveryCount);
        }

        [Fact, TestCategory("Functional")]
        public async Task DeliveryRunCleanup_ReleasesBlockedDelivery_AndIsolatesNextRun()
        {
            await WaitForStreamingProviderReadyAsync();
            var state = GetConsumerState();
            var firstDeliveryId = Guid.NewGuid().ToString();
            var firstRun = state.StartRun(firstDeliveryId, expectedDeliveries: 1, blockDeliveries: true);
            var streamId = Guid.NewGuid();
            var consumer = fixture.GrainFactory.GetGrain<IStatelessWorkerStreamConsumerGrain>(3);
            await consumer.BecomeConsumer([streamId], StreamProvider);

            try
            {
                var stream = GetStream(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId);
                await stream.OnNextAsync(firstDeliveryId);
                await firstRun.WaitForDeliveriesAsync(Timeout);
                await firstRun.ReleaseDeliveriesAsync(Timeout);

                var nextDeliveryId = Guid.NewGuid().ToString();
                var nextRun = state.StartRun(nextDeliveryId, expectedDeliveries: 1);
                await stream.OnNextAsync(firstDeliveryId);
                await stream.OnNextAsync(nextDeliveryId);
                await nextRun.WaitForDeliveriesAsync(Timeout);

                Assert.Equal(1, nextRun.DeliveryCount);
            }
            finally
            {
                await firstRun.ReleaseDeliveriesAsync(Timeout);
                await consumer.StopConsuming(streamId, StreamProvider);
            }
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

        private Task WaitForStreamingProviderReadyAsync() =>
            fixture.HostedCluster.GetSiloServiceProvider()
                .GetRequiredService<StreamingDiagnosticEventRecorder>()
                .WaitForProviderReady(StreamProvider, Timeout);

        private IAsyncStream<string> GetStream(string streamNamespace, Guid streamId) =>
            fixture.Client.GetStreamProvider(StreamProvider).GetStream<string>(streamNamespace, streamId);

        private static Guid[] CreateStreamIdsForDistinctQueues()
        {
            var mapper = new HashRingBasedStreamQueueMapper(
                new HashRingStreamQueueMapperOptions { TotalQueueCount = PartitionCount },
                StreamProvider);
            var result = new Dictionary<QueueId, Guid>();
            for (var attempt = 0; attempt < 1_000 && result.Count < PartitionCount; attempt++)
            {
                var streamId = Guid.NewGuid();
                var queueId = mapper.GetQueueForStream(
                    StreamId.Create(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId));
                result.TryAdd(queueId, streamId);
            }

            Assert.Equal(PartitionCount, result.Count);
            return result.Values.ToArray();
        }
    }
}