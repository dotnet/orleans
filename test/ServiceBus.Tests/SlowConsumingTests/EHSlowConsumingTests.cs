﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;

namespace ServiceBus.Tests.SlowConsumingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHSlowConsumingTests : OrleansTestingBase, IClassFixture<EHSlowConsumingTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();
        private static readonly TimeSpan monitorPressureWindowSize = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private const double flowControlThredhold = 0.6;
        public static readonly EventHubGeneratorStreamProviderSettings ProviderSettings =
            new EventHubGeneratorStreamProviderSettings(StreamProviderName);

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

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                ProviderSettings.WriteDataGeneratingConfig(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.AssemblyQualifiedName);

                // register stream provider
                config.Globals.RegisterStreamProvider<EHStreamProviderWithCreatedCacheList>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        private readonly Random seed;

        public EHSlowConsumingTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            seed = new Random();
        }

        [Fact, TestCategory("Functional")]
        public async Task EHSlowConsuming_ShouldFavorSlowConsumer()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), StreamNamespace, StreamProviderName);
            //set up one slow consumer grain
            var slowConsumer = this.fixture.GrainFactory.GetGrain<ISlowConsumingGrain>(Guid.NewGuid());
            await slowConsumer.BecomeConsumer(streamId.Guid, StreamNamespace, StreamProviderName);

            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            var healthyConsumers = await SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamId.Guid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //configure data generator for stream and start producing
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var randomStreamPlacementArg = new EventDataGeneratorStreamProvider.AdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
            await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderWithCreatedCacheList).FullName, StreamProviderName,
                (int)EventDataGeneratorStreamProvider.AdapterFactory.Commands.Randomly_Place_Stream_To_Queue, randomStreamPlacementArg);
            //since there's an extreme slow consumer, so the back pressure algorithm should be triggered
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(true, lastTry), timeout);

            //make slow consumer stop consuming
            await slowConsumer.StopConsuming();

            //slowConsumer stopped consuming, back pressure algorithm should be cleared in next check period.
            await Task.Delay(monitorPressureWindowSize);
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(false, lastTry), timeout);

            //clean up test
            await StopHealthyConsumerGrainComing(healthyConsumers);
            await mgmtGrain.SendControlCommandToProvider(typeof(EHStreamProviderWithCreatedCacheList).FullName, StreamProviderName,
                (int)EventDataGeneratorStreamProvider.AdapterFactory.Commands.Stop_Producing_On_Stream, streamId);
        }

        public static async Task<List<ISampleStreaming_ConsumerGrain>> SetUpHealthyConsumerGrain(IGrainFactory GrainFactory, Guid streamId, string streamNameSpace, string streamProvider, int grainCount)
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
