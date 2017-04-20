using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("Azure"), TestCategory("AzureQueue")]
    public class AQStreamingTests : TestClusterPerTest
    {
        public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        public const string SmsStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        private readonly SingleStreamTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            TestUtils.CheckForAzureStorage();
            var options = new TestClusterOptions(initialSilosCount: 2);

            options.ClusterConfiguration.AddMemoryStorageProvider();

            options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore", deleteOnClear : true);
            options.ClusterConfiguration.AddAzureTableStorageProvider("PubSubStore", deleteOnClear: true, useJsonFormat: false);

            options.ClusterConfiguration.AddSimpleMessageStreamProvider(SmsStreamProviderName, fireAndForgetDelivery: false);
            options.ClientConfiguration.AddSimpleMessageStreamProvider(SmsStreamProviderName, fireAndForgetDelivery: false);

            options.ClusterConfiguration.AddAzureQueueStreamProviderV2(AzureQueueStreamProviderName);
            options.ClientConfiguration.AddAzureQueueStreamProviderV2(AzureQueueStreamProviderName);

            return new TestCluster(options);
        }

        public AQStreamingTests()
        {
            runner = new SingleStreamTestRunner(this.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }
        
        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
        }

        ////------------------------ One to One ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }

        //[SkippableFact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task AQ_18_MultipleStreams_1J_1F_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 18, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo,
                this.HostedCluster.StopSilo);
        }

        [SkippableFact]
        public async Task AQ_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [SkippableFact]
        public async Task AQ_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [SkippableFact(Skip = "Ignored"), TestCategory("Failures")]
        public async Task AQ_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }
    }
}