using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
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

        private SingleStreamTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(initialSilosCount: 2);

            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");

            options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore", deleteOnClear : true);
            options.ClusterConfiguration.AddAzureTableStorageProvider("PubSubStore", deleteOnClear: true, useJsonFormat: false);

            options.ClusterConfiguration.AddSimpleMessageStreamProvider(SmsStreamProviderName, fireAndForgetDelivery: false);

            options.ClusterConfiguration.AddAzureQueueStreamProvider(AzureQueueStreamProviderName);
            options.ClusterConfiguration.AddAzureQueueStreamProvider("AzureQueueProvider2");

            options.ClusterConfiguration.Globals.MaxMessageBatchingSize = 100;

            options.ClientConfiguration.AddSimpleMessageStreamProvider(SmsStreamProviderName, fireAndForgetDelivery: false);
            options.ClientConfiguration.AddAzureQueueStreamProvider(AzureQueueStreamProviderName);

            return new TestCluster(options);
        }

        public AQStreamingTests()
        {
            runner = new SingleStreamTestRunner(this.HostedCluster.InternalGrainFactory, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }
        
        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
        }

        ////------------------------ One to One ----------------------//

        [Fact, TestCategory("Functional")]
        public async Task AQ_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [Fact, TestCategory("Functional")]
        public async Task AQ_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [Fact, TestCategory("Functional")]
        public async Task AQ_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact, TestCategory("Functional")]
        public async Task AQ_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [Fact, TestCategory("Functional")]
        public async Task AQ_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.HostedCluster.InternalGrainFactory, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.HostedCluster.InternalGrainFactory, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }

        //[Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task AQ_18_MultipleStreams_1J_1F_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.HostedCluster.InternalGrainFactory, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 18, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo,
                this.HostedCluster.StopSilo);
        }

        [Fact]
        public async Task AQ_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [Fact]
        public async Task AQ_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [Fact(Skip = "Ignored"), TestCategory("Failures")]
        public async Task AQ_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }
    }
}