
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using OrleansServiceBusUtils.Providers.Streams.EventHub;
using Tester;
using Orleans.Runtime;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class EHSubscriptionMultiplicityTestsFixture : BaseClusterFixture
    {
        public const string StreamProviderName = "EventHubStreamProvider";
        public const string StreamNamespace = "EHSubscriptionMultiplicityTestsNamespace";
        public const string EHPath = "ehorleanstest";
        public const string EHConsumerGroup = "orleansnightly";

        public static readonly EventHubStreamProviderConfig ProviderConfig =
            new EventHubStreamProviderConfig(StreamProviderName);

        public static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString,
            EHConsumerGroup, EHPath);

        public EHSubscriptionMultiplicityTestsFixture()
            : base(new TestingSiloHost(
                new TestingSiloOptions
                {
                    StartFreshOrleans = true,
                    SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
                    AdjustConfig = AdjustClusterConfiguration
                }))
        {
        }

        public static void AdjustClusterConfiguration(ClusterConfiguration config)
        {
            var settings = new Dictionary<string, string>();
            // get initial settings from configs
            ProviderConfig.WriteProperties(settings);
            EventHubConfig.WriteProperties(settings);

            // add queue balancer setting
            settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

            // register stream provider
            config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, settings);
            config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");

            // Make sure a node config exist for each silo in the cluster.
            // This is required for the DynamicClusterConfigDeploymentBalancer to properly balance queues.
            config.GetOrCreateNodeConfigurationForSilo("Primary");
            config.GetOrCreateNodeConfigurationForSilo("Secondary_1");
        }
    }

    public class EHSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<EHSubscriptionMultiplicityTestsFixture>
    {
        private readonly SubscriptionMultiplicityTestRunner runner;

        public EHSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(EHSubscriptionMultiplicityTestsFixture.StreamProviderName, GrainClient.Logger);            
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleParallelSubscriptionTest()
        {
            logger.Info("************************ EHMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleLinearSubscriptionTest()
        {
            logger.Info("************************ EHMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleSubscriptionTest_AddRemove()
        {
            logger.Info("************************ EHMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionTest()
        {
            logger.Info("************************ EHResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ EHResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHActiveSubscriptionTest()
        {
            logger.Info("************************ EHActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), EHSubscriptionMultiplicityTestsFixture.StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHTwoIntermitentStreamTest()
        {
            logger.Info("************************ EHTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }
    }
}
