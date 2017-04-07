using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;

namespace ServiceBus.Tests.SlowConsumingTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class EHSlowConsumingTests : OrleansTestingBase, IClassFixture<EHSlowConsumingTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();
        private static readonly TimeSpan monitorPressureWindowSize = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private const double flowControlThredhold = 0.6;
        public static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName);

        private static readonly Lazy<EventHubSettings> EventHubConfig = new Lazy<EventHubSettings>(() =>
            new EventHubSettings(
                TestDefaultConfiguration.EventHubConnectionString,
                EHConsumerGroup, EHPath));

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(TestDefaultConfiguration.DataConnectionString,
                EHCheckpointTable, CheckpointNamespace, TimeSpan.FromSeconds(1));


        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                ProviderSettings.SlowConsumingMonitorPressureWindowSize = monitorPressureWindowSize;
                ProviderSettings.SlowConsumingMonitorFlowControlThreshold = flowControlThredhold;
                ProviderSettings.AveragingCachePressureMonitorFlowControlThreshold = null;
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private bool isSkippable;
            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.EventHubConnectionString) ||
                    string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
                {
                    this.isSkippable = true;
                    throw new SkipException("EventHubConnectionString or DataConnectionString is not set up");
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                if (!isSkippable)
                {
                    var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString);
                    dataManager.InitTableAsync().Wait();
                    dataManager.ClearTableAsync().Wait();
                }
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                EventHubConfig.Value.WriteProperties(settings);
                CheckpointerSettings.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // register stream provider
                config.Globals.RegisterStreamProvider<EHStreamProviderWithCreatedCacheList>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        public EHSlowConsumingTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
        public async Task EHSlowConsuming_ShouldFavorSlowConsumer()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), StreamNamespace, StreamProviderName);
            //set up one slow consumer grain
            var slowConsumer = this.fixture.GrainFactory.GetGrain<ISlowConsumingGrain>(Guid.NewGuid());
            await slowConsumer.BecomeConsumer(streamId.Guid, StreamNamespace, StreamProviderName);

            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            var healthyConsumers = await SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //set up producer and start producing
            var producer = this.fixture.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, StreamNamespace, StreamProviderName);
            await producer.StartPeriodicProducing();

            //since there's an extreme slow consumer, so the back pressure algorithm should be triggered
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(true, lastTry), timeout);

            //make slow consumer stop consuming
            await slowConsumer.StopConsuming();

            //slowConsumer stopped consuming, back pressure algorithm should be cleared in next check period.
            await Task.Delay(monitorPressureWindowSize);
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(false, lastTry), timeout);

            //clean up test
            await producer.StopPeriodicProducing();
            await StopHealthyConsumerGrainComing(healthyConsumers);
        }

        private async Task<List<ISampleStreaming_ConsumerGrain>> SetUpHealthyConsumerGrain(IGrainFactory GrainFactory, Guid streamId, string streamNameSpace, string streamProvider, int grainCount)
        {
            List<ISampleStreaming_ConsumerGrain> grains = new List<ISampleStreaming_ConsumerGrain>();
            List<Task> tasks = new List<Task>();
            while (grainCount > 0)
            {
                var consumer = GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
                grains.Add(consumer);
                tasks.Add(consumer.BecomeConsumer(streamId, streamNameSpace, streamProvider));
                grainCount--;
            }
            await Task.WhenAll(tasks);
            return grains;
        }

        private async Task StopHealthyConsumerGrainComing(List<ISampleStreaming_ConsumerGrain> grains)
        {
            List<Task> tasks = new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.StopConsuming());
            }
            await Task.WhenAll(tasks);
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
            object[] replies = await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderWithCreatedCacheList).FullName,
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
