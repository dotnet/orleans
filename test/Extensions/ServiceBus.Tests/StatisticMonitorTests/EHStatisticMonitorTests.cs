using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;
using ServiceBus.Tests.SlowConsumingTests;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Configuration;

namespace ServiceBus.Tests.MonitorTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStatisticMonitorTests : OrleansTestingBase, IClassFixture<EHStatisticMonitorTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(5);
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
                            services.AddTransientNamedService(StreamProviderName, (s, n) => SimpleStreamEventDataGenerator.CreateFactory(s));
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

        [Fact(Skip = "See https://github.com/dotnet/orleans/issues/4594"), TestCategory("Functional")]
        public async Task EHStatistics_MonitorCalledAccordingly()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), StreamNamespace, StreamProviderName);
            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            _ = await EHSlowConsumingTests.SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //configure data generator for stream and start producing
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var randomStreamPlacementArg = new EHStreamProviderForMonitorTestsAdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
            await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.Commands.Randomly_Place_Stream_To_Queue, randomStreamPlacementArg);

            // let the test to run for a while to build up some streaming traffic
            await Task.Delay(timeout);
            //wait sometime after cache pressure changing, for the system to notice it and trigger cache monitor to track it
            await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.ChangeCachePressure, null);
            await Task.Delay(timeout);

            //assert EventHubReceiverMonitor call counters
            var receiverMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetReceiverMonitorCallCounters, null);
            foreach (var callCounter in receiverMonitorCounters)
            {
                AssertReceiverMonitorCallCounters(callCounter as EventHubReceiverMonitorCounters);
            }

            var cacheMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetCacheMonitorCallCounters, null);
            foreach (var callCounter in cacheMonitorCounters)
            {
                AssertCacheMonitorCallCounters(callCounter as CacheMonitorCounters);
            }

            var objectPoolMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
             (int)EHStreamProviderForMonitorTestsAdapterFactory.QueryCommands.GetObjectPoolMonitorCallCounters, null);
            foreach (var callCounter in objectPoolMonitorCounters)
            {
                AssertObjectPoolMonitorCallCounters(callCounter as ObjectPoolMonitorCounters);
            }
        }

        private void AssertCacheMonitorCallCounters(CacheMonitorCounters totalCacheMonitorCallCounters)
        {
            var c = totalCacheMonitorCallCounters;
            Assert.True(c.TrackCachePressureMonitorStatusChangeCallCounter > 0,
                $"Expected {nameof(c.TrackCachePressureMonitorStatusChangeCallCounter)} > 0, got {c.TrackCachePressureMonitorStatusChangeCallCounter}");
            Assert.True(c.TrackMemoryAllocatedCallCounter > 0, $"Expected {nameof(c.TrackMemoryAllocatedCallCounter)} > 0, got {c.TrackMemoryAllocatedCallCounter}");
            Assert.True(0 == c.TrackMemoryReleasedCallCounter, $"Expected {nameof(c.TrackMemoryReleasedCallCounter)} == 0, got {c.TrackMemoryReleasedCallCounter}");
            Assert.True(c.TrackMessageAddedCounter > 0, $"Expected {nameof(c.TrackMessageAddedCounter)} > 0, got {c.TrackMessageAddedCounter}");
            Assert.True(0 == c.TrackMessagePurgedCounter, $"Expected {nameof(c.TrackMessagePurgedCounter)} == 0, got {c.TrackMessagePurgedCounter}");
        }

        private void AssertReceiverMonitorCallCounters(EventHubReceiverMonitorCounters totalReceiverMonitorCallCounters)
        {
            var c = totalReceiverMonitorCallCounters;
            Assert.True(ehPartitionCountPerSilo == c.TrackInitializationCallCounter, $"Expected {nameof(c.TrackInitializationCallCounter)} == {ehPartitionCountPerSilo}, got {c.TrackInitializationCallCounter}");
            Assert.True(c.TrackMessagesReceivedCallCounter > 0, $"Expected {nameof(c.TrackMessagesReceivedCallCounter)} > 0, got {c.TrackMessagesReceivedCallCounter}");
            Assert.True(c.TrackReadCallCounter > 0, $"Expected {nameof(c.TrackReadCallCounter)} > 0, got {c.TrackReadCallCounter}");
            Assert.True(0 == c.TrackShutdownCallCounter, $"Expected {nameof(c.TrackShutdownCallCounter)} == 0, got {c.TrackShutdownCallCounter}");
        }

        private void AssertObjectPoolMonitorCallCounters(ObjectPoolMonitorCounters totalObjectPoolMonitorCallCounters)
        {
            var c = totalObjectPoolMonitorCallCounters;
            Assert.True(c.TrackObjectAllocatedByCacheCallCounter > 0, $"Expected {nameof(c.TrackObjectAllocatedByCacheCallCounter)} > 0, got {c.TrackObjectAllocatedByCacheCallCounter}");
            Assert.True(0 == c.TrackObjectReleasedFromCacheCallCounter, $"Expected {nameof(c.TrackObjectReleasedFromCacheCallCounter)} == 0, got {c.TrackObjectReleasedFromCacheCallCounter}");
        }
    }
}
