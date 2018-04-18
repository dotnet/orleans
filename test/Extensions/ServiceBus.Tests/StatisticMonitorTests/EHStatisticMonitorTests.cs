using System;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;
using ServiceBus.Tests.SlowConsumingTests;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.ServiceBus.Providers.Testing;
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


            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(StreamProviderName, EHStreamProviderForMonitorTestsAdapterFactory.Create, b=>b
                        .ConfigureComponent<IStreamQueueCheckpointerFactory>((s, n) => NoOpCheckpointerFactory.Instance)
                        .Configure<StreamStatisticOptions>(ob => ob.Configure(options => options.StatisticMonitorWriteInterval = monitorWriteInterval))
                        .UseDynamicClusterConfigDeploymentBalancer());
                    hostBuilder
                        .ConfigureServices(services =>
                        {
                            services.AddTransientNamedService<Func<IStreamIdentity, IStreamDataGenerator<EventData>>>(StreamProviderName, (s, n) => SimpleStreamEventDataGenerator.CreateFactory(s));
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
            var healthyConsumers = await EHSlowConsumingTests.SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

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
            Assert.True(totalCacheMonitorCallCounters.TrackCachePressureMonitorStatusChangeCallCounter > 0);
            Assert.True(totalCacheMonitorCallCounters.TrackMemoryAllocatedCallCounter > 0);
            Assert.Equal(0, totalCacheMonitorCallCounters.TrackMemoryReleasedCallCounter);
            Assert.True(totalCacheMonitorCallCounters.TrackMessageAddedCounter > 0);
            Assert.Equal(0, totalCacheMonitorCallCounters.TrackMessagePurgedCounter);
        }

        private void AssertReceiverMonitorCallCounters(EventHubReceiverMonitorCounters totalReceiverMonitorCallCounters)
        {
            Assert.Equal(ehPartitionCountPerSilo, totalReceiverMonitorCallCounters.TrackInitializationCallCounter);
            Assert.True(totalReceiverMonitorCallCounters.TrackMessagesReceivedCallCounter > 0);
            Assert.True(totalReceiverMonitorCallCounters.TrackReadCallCounter > 0);
            Assert.Equal(0, totalReceiverMonitorCallCounters.TrackShutdownCallCounter);
        }

        private void AssertObjectPoolMonitorCallCounters(ObjectPoolMonitorCounters totalObjectPoolMonitorCallCounters)
        {
            Assert.True(totalObjectPoolMonitorCallCounters.TrackObjectAllocatedByCacheCallCounter > 0);
            Assert.Equal(0, totalObjectPoolMonitorCallCounters.TrackObjectReleasedFromCacheCallCounter);
        }
    }
}
