using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;
using Orleans.TestingHost.Utils;
using UnitTests.Grains;
using ServiceBus.Tests.SlowConsumingTests;

namespace ServiceBus.Tests.MonitorTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStatisticMonitorTests : OrleansTestingBase, IClassFixture<EHStatisticMonitorTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan monitorWriteInterval = TimeSpan.FromSeconds(2);
        private static readonly int ehPartitionCountPerSilo = 4;
        public static readonly EventHubGeneratorStreamProviderSettings ProviderSettings =
            new EventHubGeneratorStreamProviderSettings(StreamProviderName);


        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                ProviderSettings.StatisticMonitorWriteInterval = monitorWriteInterval;
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                ProviderSettings.WriteDataGeneratingConfig(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // register stream provider
                config.Globals.RegisterStreamProvider<EHStreamProviderForMonitorTests>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
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
            //set up one slow consumer grain
            var slowConsumer = this.fixture.GrainFactory.GetGrain<ISlowConsumingGrain>(Guid.NewGuid());
            await slowConsumer.BecomeConsumer(streamId.Guid, StreamNamespace, StreamProviderName);

            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            var healthyConsumers = await EHSlowConsumingTests.SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //configure data generator for stream and start producing
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var randomStreamPlacementArg = new EventDataGeneratorStreamProvider.AdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
            await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderForMonitorTests).FullName, StreamProviderName,
                (int)EventDataGeneratorStreamProvider.AdapterFactory.Commands.Randomly_Place_Stream_To_Queue, randomStreamPlacementArg);
            //since there's an extreme slow consumer, so the back pressure algorithm should be triggered
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(true, lastTry), timeout);

            //make slow consumer stop consuming
            await slowConsumer.StopConsuming();

            //assert EventHubReceiverMonitor call counters
            var receiverMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderForMonitorTests).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTests.AdapterFactory.QueryCommands.GetReceiverMonitorCallCounters, null);
            foreach (var callCounter in receiverMonitorCounters)
            {
                AssertReceiverMonitorCallCounters(callCounter as EventHubReceiverMonitorCounters);
            }

            var cacheMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderForMonitorTests).FullName, StreamProviderName,
                (int)EHStreamProviderForMonitorTests.AdapterFactory.QueryCommands.GetCacheMonitorCallCounters, null);
            foreach (var callCounter in cacheMonitorCounters)
            {
                AssertCacheMonitorCallCounters(callCounter as CacheMonitorCounters);
            }

            var objectPoolMonitorCounters = await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderForMonitorTests).FullName, StreamProviderName,
             (int)EHStreamProviderForMonitorTests.AdapterFactory.QueryCommands.GetObjectPoolMonitorCallCounters, null);
            foreach (var callCounter in objectPoolMonitorCounters)
            {
                AssertObjectPoolMonitorCallCounters(callCounter as ObjectPoolMonitorCounters);
            }
        }

        private void AssertCacheMonitorCallCounters(CacheMonitorCounters totalCacheMonitorCallCounters)
        {
            Assert.True(totalCacheMonitorCallCounters.TrackCachePressureMonitorStatusChangeCallCounter > 0);
            Assert.True(totalCacheMonitorCallCounters.TrackMemoryAllocatedCallCounter > 0);
            Assert.Equal(totalCacheMonitorCallCounters.TrackMemoryReleasedCallCounter, 0);
            Assert.True(totalCacheMonitorCallCounters.TrackMessageAddedCounter > 0);
            Assert.Equal(totalCacheMonitorCallCounters.TrackMessagePurgedCounter, 0);
        }

        private void AssertReceiverMonitorCallCounters(EventHubReceiverMonitorCounters totalReceiverMonitorCallCounters)
        {
            Assert.Equal(totalReceiverMonitorCallCounters.TrackInitializationCallCounter, ehPartitionCountPerSilo);
            Assert.True(totalReceiverMonitorCallCounters.TrackMessagesReceivedCallCounter > 0);
            Assert.True(totalReceiverMonitorCallCounters.TrackReadCallCounter > 0);
            Assert.Equal(totalReceiverMonitorCallCounters.TrackShutdownCallCounter, 0);
        }

        private void AssertObjectPoolMonitorCallCounters(ObjectPoolMonitorCounters totalObjectPoolMonitorCallCounters)
        {
            Assert.True(totalObjectPoolMonitorCallCounters.TrackObjectAllocatedByCacheCallCounter > 0);
            Assert.Equal(totalObjectPoolMonitorCallCounters.TrackObjectReleasedFromCacheCallCounter, 0);
        }

        private async Task<bool> AssertCacheBackPressureTriggered(bool expectedResult, bool assertIsTrue)
        {
            if (assertIsTrue)
            {
                bool actualResult = await IsBackPressureTriggered();
                Assert.True(expectedResult == actualResult, $"Back pressure algorithm should be triggered? expected: {expectedResult}, actual: {actualResult}");
                return true;
            }
            else
            {
                return (await IsBackPressureTriggered()) == expectedResult;
            }
        }

        private async Task<bool> IsBackPressureTriggered()
        {
            IManagementGrain mgmtGrain = this.fixture.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] replies = await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderForMonitorTests).FullName,
                             StreamProviderName, EHStreamProviderWithCreatedCacheList.AdapterFactory.IsCacheBackPressureTriggeredCommand, null);
            foreach (var re in replies)
            {
                if ((bool)re)
                    return true;
            }
            return false;
        }
    }
}
