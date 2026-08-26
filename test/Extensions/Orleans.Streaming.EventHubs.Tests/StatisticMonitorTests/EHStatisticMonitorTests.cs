using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;
using ServiceBus.Tests.SlowConsumingTests;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceBus.Tests.MonitorTests
{
    /// <summary>
    /// Tests for EventHub statistics monitoring including receiver, cache, and object pool monitor counters.
    /// </summary>
    [TestCategory("EventHub"), TestCategory("Streaming")]
    [TestSuite("Functional")]
    [TestProvider("EventHub")]
    [TestArea("Streaming")]
    public class EHStatisticMonitorTests : OrleansTestingBase, IClassFixture<EHStatisticMonitorTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan monitorWriteInterval = TimeSpan.FromSeconds(2);
        private static readonly int ehPartitionCountPerSilo = 4;

        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }


            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(
                            StreamProviderName,
                            EHStreamProviderForMonitorTestsAdapterFactory.Create,
                            b=>
                            {
                                b.ConfigureComponent<IStreamQueueCheckpointerFactory>((s, n) => NoOpCheckpointerFactory.Instance);
                                b.Configure<StreamStatisticOptions>(ob => ob.Configure(options => options.StatisticMonitorWriteInterval = monitorWriteInterval));
                                b.UseDynamicClusterConfigDeploymentBalancer();
                            });
                    hostBuilder
                        .ConfigureServices(services =>
                        {
                            services.AddKeyedTransient(StreamProviderName, (s, n) => SimpleStreamEventDataGenerator.CreateFactory(s));
                        })
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

        }

        private readonly Random seed;

        public EHStatisticMonitorTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            seed = new Random();
        }

        [Fact, TestCategory("Functional")]
        public async Task EHStatistics_MonitorCalledAccordingly()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), StreamNamespace, StreamProviderName);
            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            _ = await EHSlowConsumingTests.SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //configure data generator for stream and start producing
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            await TestingUtils.WaitUntilAsync(
                (lastTry, cancellationToken) => CheckReceiversInitialized(mgmtGrain, lastTry, cancellationToken),
                timeout,
                cancellationToken: TestContext.Current.CancellationToken);
            var randomStreamPlacementArg = new EHStreamProviderForMonitorTestsAdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
            await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.Commands.Randomly_Place_Stream_To_Queue, randomStreamPlacementArg);
            await TestingUtils.WaitUntilAsync(
                (lastTry, cancellationToken) => CheckMonitorCounters(mgmtGrain, requireCachePressure: false, lastTry, cancellationToken),
                timeout,
                cancellationToken: TestContext.Current.CancellationToken);

            await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.ChangeCachePressure, null);
            await TestingUtils.WaitUntilAsync(
                (lastTry, cancellationToken) => CheckMonitorCounters(mgmtGrain, requireCachePressure: true, lastTry, cancellationToken),
                timeout,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        private static async Task<bool> CheckReceiversInitialized(IManagementGrain mgmtGrain, bool lastTry, CancellationToken cancellationToken)
        {
            var receiverMonitorCounters = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetReceiverMonitorCallCounters, null).WaitAsync(cancellationToken);
            var ready = receiverMonitorCounters.Length > 0
                && receiverMonitorCounters.All(callCounter =>
                    ((EventHubReceiverMonitorCounters)callCounter!).TrackInitializationCallCounter == ehPartitionCountPerSilo);

            if (ready || lastTry)
            {
                Assert.NotEmpty(receiverMonitorCounters);
                foreach (var callCounter in receiverMonitorCounters)
                {
                    var c = (EventHubReceiverMonitorCounters)callCounter!;
                    Assert.True(c.TrackInitializationCallCounter == ehPartitionCountPerSilo,
                        $"Expected {nameof(c.TrackInitializationCallCounter)} == {ehPartitionCountPerSilo}, got {c.TrackInitializationCallCounter}");
                }
            }

            return ready;
        }

        private static async Task<bool> CheckMonitorCounters(
            IManagementGrain mgmtGrain,
            bool requireCachePressure,
            bool lastTry,
            CancellationToken cancellationToken)
        {
            var receiverMonitorCounters = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetReceiverMonitorCallCounters, null).WaitAsync(cancellationToken);
            var cacheMonitorCounters = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetCacheMonitorCallCounters, null).WaitAsync(cancellationToken);
            var objectPoolMonitorCounters = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetObjectPoolMonitorCallCounters, null).WaitAsync(cancellationToken);

            var ready = receiverMonitorCounters.Length > 0
                && receiverMonitorCounters.All(callCounter => ReceiverMonitorCallCountersAreExpected((EventHubReceiverMonitorCounters)callCounter!))
                && cacheMonitorCounters.Length > 0
                && cacheMonitorCounters.All(callCounter => CacheMonitorCallCountersAreExpected((CacheMonitorCounters)callCounter!, requireCachePressure))
                && objectPoolMonitorCounters.Length > 0
                && objectPoolMonitorCounters.All(callCounter => ObjectPoolMonitorCallCountersAreExpected((ObjectPoolMonitorCounters)callCounter!));

            if (ready || lastTry)
            {
                Assert.NotEmpty(receiverMonitorCounters);
                foreach (var callCounter in receiverMonitorCounters)
                {
                    AssertReceiverMonitorCallCounters((EventHubReceiverMonitorCounters)callCounter!);
                }

                Assert.NotEmpty(cacheMonitorCounters);
                foreach (var callCounter in cacheMonitorCounters)
                {
                    AssertCacheMonitorCallCounters((CacheMonitorCounters)callCounter!, requireCachePressure);
                }

                Assert.NotEmpty(objectPoolMonitorCounters);
                foreach (var callCounter in objectPoolMonitorCounters)
                {
                    AssertObjectPoolMonitorCallCounters((ObjectPoolMonitorCounters)callCounter!);
                }
            }

            return ready;
        }

        private static bool CacheMonitorCallCountersAreExpected(CacheMonitorCounters c, bool requireCachePressure) =>
            (!requireCachePressure || c.TrackCachePressureMonitorStatusChangeCallCounter > 0)
            && c.TrackMemoryAllocatedCallCounter > 0
            && c.TrackMemoryReleasedCallCounter == 0
            && c.TrackMessageAddedCounter > 0
            && c.TrackMessagePurgedCounter == 0;

        private static bool ReceiverMonitorCallCountersAreExpected(EventHubReceiverMonitorCounters c) =>
            c.TrackInitializationCallCounter == ehPartitionCountPerSilo
            && c.TrackMessagesReceivedCallCounter > 0
            && c.TrackReadCallCounter > 0
            && c.TrackShutdownCallCounter == 0;

        private static bool ObjectPoolMonitorCallCountersAreExpected(ObjectPoolMonitorCounters c) =>
            c.TrackObjectAllocatedByCacheCallCounter > 0
            && c.TrackObjectReleasedFromCacheCallCounter == 0;

        private static void AssertCacheMonitorCallCounters(CacheMonitorCounters totalCacheMonitorCallCounters, bool requireCachePressure)
        {
            var c = totalCacheMonitorCallCounters;
            if (requireCachePressure)
            {
                Assert.True(c.TrackCachePressureMonitorStatusChangeCallCounter > 0,
                    $"Expected {nameof(c.TrackCachePressureMonitorStatusChangeCallCounter)} > 0, got {c.TrackCachePressureMonitorStatusChangeCallCounter}");
            }

            Assert.True(c.TrackMemoryAllocatedCallCounter > 0, $"Expected {nameof(c.TrackMemoryAllocatedCallCounter)} > 0, got {c.TrackMemoryAllocatedCallCounter}");
            Assert.True(0 == c.TrackMemoryReleasedCallCounter, $"Expected {nameof(c.TrackMemoryReleasedCallCounter)} == 0, got {c.TrackMemoryReleasedCallCounter}");
            Assert.True(c.TrackMessageAddedCounter > 0, $"Expected {nameof(c.TrackMessageAddedCounter)} > 0, got {c.TrackMessageAddedCounter}");
            Assert.True(0 == c.TrackMessagePurgedCounter, $"Expected {nameof(c.TrackMessagePurgedCounter)} == 0, got {c.TrackMessagePurgedCounter}");
        }

        private static void AssertReceiverMonitorCallCounters(EventHubReceiverMonitorCounters totalReceiverMonitorCallCounters)
        {
            var c = totalReceiverMonitorCallCounters;
            Assert.True(ehPartitionCountPerSilo == c.TrackInitializationCallCounter, $"Expected {nameof(c.TrackInitializationCallCounter)} == {ehPartitionCountPerSilo}, got {c.TrackInitializationCallCounter}");
            Assert.True(c.TrackMessagesReceivedCallCounter > 0, $"Expected {nameof(c.TrackMessagesReceivedCallCounter)} > 0, got {c.TrackMessagesReceivedCallCounter}");
            Assert.True(c.TrackReadCallCounter > 0, $"Expected {nameof(c.TrackReadCallCounter)} > 0, got {c.TrackReadCallCounter}");
            Assert.True(0 == c.TrackShutdownCallCounter, $"Expected {nameof(c.TrackShutdownCallCounter)} == 0, got {c.TrackShutdownCallCounter}");
        }

        private static void AssertObjectPoolMonitorCallCounters(ObjectPoolMonitorCounters totalObjectPoolMonitorCallCounters)
        {
            var c = totalObjectPoolMonitorCallCounters;
            Assert.True(c.TrackObjectAllocatedByCacheCallCounter > 0, $"Expected {nameof(c.TrackObjectAllocatedByCacheCallCounter)} > 0, got {c.TrackObjectAllocatedByCacheCallCounter}");
            Assert.True(0 == c.TrackObjectReleasedFromCacheCallCounter, $"Expected {nameof(c.TrackObjectReleasedFromCacheCallCounter)} == 0, got {c.TrackObjectReleasedFromCacheCallCounter}");
        }
    }
}
