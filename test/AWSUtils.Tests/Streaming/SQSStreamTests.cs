#if !NETSTANDARD_TODO
using System.Collections.Generic;
using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Orleans.Providers.Streams;
using Orleans.Storage;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using UnitTests.StreamingTests;
using Xunit;
using Orleans.Runtime.Configuration;
using TestExtensions;
using UnitTests.Streaming;

namespace AWSUtils.Tests.Streaming
{
    [TestCategory("AWS"), TestCategory("SQS")]
    public class SQSStreamTests : TestClusterPerTest
    {
        public static readonly string SQS_STREAM_PROVIDER_NAME = "SQSProvider";

        private SingleStreamTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            //from the config files
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);

            options.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);

            options.ClientConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);

            //previous silo creation
            options.ClusterConfiguration.Globals.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
            options.ClientConfiguration.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
            var streamConnectionString = new Dictionary<string, string>
                {
                    { "DataConnectionString",  AWSTestConstants.DefaultSQSConnectionString}
                };

            options.ClientConfiguration.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);

            options.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);
            options.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider2", streamConnectionString);

            var storageConnectionString = new Dictionary<string, string>
                {
                    { "DataConnectionString",  $"Service={AWSTestConstants.Service}"},
                    { "DeleteStateOnClear",  "true"}
                };
            options.ClusterConfiguration.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("DynamoDBStore", storageConnectionString);
            var storageConnectionString2 = new Dictionary<string, string>
                {
                    { "DataConnectionString",  $"Service={AWSTestConstants.Service}"},
                    { "DeleteStateOnClear",  "true"},
                    { "UseJsonFormat",  "true"}
                };
            options.ClusterConfiguration.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("PubSubStore", storageConnectionString2);

            return new TestCluster(options);
        }


        public SQSStreamTests()
        {
            runner = new SingleStreamTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME);
        }

        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQS_STREAM_PROVIDER_NAME, deploymentId, AWSTestConstants.DefaultSQSConnectionString).Wait();
        }

        ////------------------------ One to One ----------------------//

        [Fact]
        public async Task SQS_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [Fact]
        public async Task SQS_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [Fact]
        public async Task SQS_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [Fact]
        public async Task SQS_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [Fact]
        public async Task SQS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact]
        public async Task SQS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [Fact]
        public async Task SQS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [Fact]
        public async Task SQS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [Fact]
        public async Task SQS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact]
        public async Task SQS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [Fact]
        public async Task SQS_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [Fact]
        public async Task SQS_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact]
        public async Task SQS_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [Fact]
        public async Task SQS_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [Fact]
        public async Task SQS_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [Fact]
        public async Task SQS_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact]
        public async Task SQS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }
    }
}

#endif