using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orleans.Providers.Streams;
using Orleans.Storage;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using UnitTests.StorageTests.AWSUtils;
using UnitTests.StreamingTests;
using Xunit;

namespace UnitTests.Streaming
{
    [TestCategory("AWS"), TestCategory("SQS")]
    public class SQSStreamTests : HostedTestClusterPerTest
    {
        internal static readonly FileInfo SiloConfigFile = new FileInfo("Config_SQSStreamProviders.xml");
        internal static readonly FileInfo ClientConfigFile = new FileInfo("ClientConfig_SQSStreamProviders.xml");
        public static readonly string SQS_STREAM_PROVIDER_NAME = "SQSProvider";

        private static readonly TestingSiloOptions sqsSiloOptions = new TestingSiloOptions
        {
            SiloConfigFile = SiloConfigFile,
        };
        private static readonly TestingClientOptions sqsClientOptions = new TestingClientOptions
        {
            ClientConfigFile = ClientConfigFile
        };

        private SingleStreamTestRunner runner;

        public override TestingSiloHost CreateSiloHost()
        {
            var streamConnectionString = new Dictionary<string, string>
                {
                    { "DataConnectionString",  AWSTestConstants.DefaultSQSConnectionString}
                };

            sqsClientOptions.AdjustConfig = config =>
            {
                config.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                config.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);
            };
            
            sqsSiloOptions.AdjustConfig = config =>
            {
                config.Globals.DataConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                config.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider", streamConnectionString);
                config.Globals.RegisterStreamProvider<SQSStreamProvider>("SQSProvider2", streamConnectionString);
                var storageConnectionString = new Dictionary<string, string>
                {
                    { "DataConnectionString",  $"Service={AWSTestConstants.Service}"},
                    { "DeleteStateOnClear",  "true"}
                };
                config.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("DynamoDBStore", storageConnectionString);
                var storageConnectionString2 = new Dictionary<string, string>
                {
                    { "DataConnectionString",  $"Service={AWSTestConstants.Service}"},
                    { "DeleteStateOnClear",  "true"},
                    { "UseJsonFormat",  "true"}
                };
                config.Globals.RegisterStorageProvider<DynamoDBStorageProvider>("PubSubStore", storageConnectionString2);
            };
            return new TestingSiloHost(sqsSiloOptions, sqsClientOptions);
        }

        public SQSStreamTests()
        {
            runner = new SingleStreamTestRunner(SQS_STREAM_PROVIDER_NAME);
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
            var multiRunner = new MultipleStreamsTestRunner(SQS_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact]
        public async Task SQS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SQS_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }
    }
}
