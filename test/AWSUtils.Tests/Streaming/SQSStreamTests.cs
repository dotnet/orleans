using System.Collections.Generic;
using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using Xunit;
using Orleans.Runtime.Configuration;
using TestExtensions;
using UnitTests.Streaming;
using OrleansAWSUtils.Streams;

namespace AWSUtils.Tests.Streaming
{
    [TestCategory("AWS"), TestCategory("SQS")]
    public class SQSStreamTests : TestClusterPerTest
    {
        public static readonly string SQS_STREAM_PROVIDER_NAME = "SQSProvider";

        private SingleStreamTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            builder.ConfigureLegacyConfiguration(options =>
            {
                //from the config files
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);

                options.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);

                options.ClientConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);

                //previous silo creation
                options.ClusterConfiguration.Globals.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                options.ClientConfiguration.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                var streamConnectionString = new Dictionary<string, string>
                {
                    {"DataConnectionString", AWSTestConstants.DefaultSQSConnectionString}
                };

                options.ClientConfiguration.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);

                options.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);
                options.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider2", streamConnectionString);

                var storageConnectionString = new Dictionary<string, string>
                {
                    {"DataConnectionString", $"Service={AWSTestConstants.Service}"},
                    {"DeleteStateOnClear", "true"}
                };
                options.ClusterConfiguration.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("DynamoDBStore", storageConnectionString);
                var storageConnectionString2 = new Dictionary<string, string>
                {
                    {"DataConnectionString", $"Service={AWSTestConstants.Service}"},
                    {"DeleteStateOnClear", "true"},
                    {"UseJsonFormat", "true"}
                };
                options.ClusterConfiguration.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("PubSubStore", storageConnectionString2);
            });
        }


        public SQSStreamTests()
        {
            runner = new SingleStreamTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME);
        }

        public override void Dispose()
        {
            var clusterId = HostedCluster.ClusterId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQS_STREAM_PROVIDER_NAME, clusterId, AWSTestConstants.DefaultSQSConnectionString, NullLoggerFactory.Instance).Wait();
        }

        ////------------------------ One to One ----------------------//

        [SkippableFact]
        public async Task SQS_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [SkippableFact]
        public async Task SQS_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [SkippableFact]
        public async Task SQS_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [SkippableFact]
        public async Task SQS_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [SkippableFact]
        public async Task SQS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task SQS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [SkippableFact]
        public async Task SQS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task SQS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [SkippableFact]
        public async Task SQS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task SQS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [SkippableFact]
        public async Task SQS_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [SkippableFact]
        public async Task SQS_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [SkippableFact]
        public async Task SQS_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [SkippableFact]
        public async Task SQS_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [SkippableFact]
        public async Task SQS_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [SkippableFact]
        public async Task SQS_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task SQS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }
    }
}
