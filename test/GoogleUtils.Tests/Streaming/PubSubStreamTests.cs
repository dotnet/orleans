using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace GoogleUtils.Tests.Streaming
{
    [TestCategory("GCP"), TestCategory("PubSub")]
    public class PubSubStreamTests : TestClusterPerTest
    {
        public static readonly string PUBSUB_STREAM_PROVIDER_NAME = "GPSProvider";
        private SingleStreamTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!GoogleTestUtils.IsPubSubSimulatorAvailable.Value)
            {
                throw new SkipException("Google PubSub Simulator not available");
            }

            builder.ConfigureLegacyConfiguration(legacy =>
            {
                //from the config files
                legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);
                legacy.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);
                legacy.ClientConfiguration.AddSimpleMessageStreamProvider("SMSProvider", fireAndForgetDelivery: false);

                var providerSettings = new Dictionary<string, string>
                {
                    {"ProjectId", GoogleTestUtils.ProjectId},
                    {"TopicId", GoogleTestUtils.TopicId},
                    {"DeploymentId", GoogleTestUtils.DeploymentId.ToString()},
                    {"Deadline", "600"},
                    //{ "CustomEndpoint", "localhost:8085" }
                };

                legacy.ClientConfiguration.RegisterStreamProvider<PubSubStreamProvider>(PUBSUB_STREAM_PROVIDER_NAME, providerSettings);
                legacy.ClusterConfiguration.Globals.RegisterStreamProvider<PubSubStreamProvider>(PUBSUB_STREAM_PROVIDER_NAME, providerSettings);
                legacy.ClusterConfiguration.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            });
        }

        public PubSubStreamTests()
        {
            runner = new SingleStreamTestRunner(InternalClient, PUBSUB_STREAM_PROVIDER_NAME);
        }

        ////------------------------ One to One ----------------------//

        [SkippableFact]
        public async Task GPS_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [SkippableFact]
        public async Task GPS_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [SkippableFact]
        public async Task GPS_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [SkippableFact]
        public async Task GPS_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [SkippableFact]
        public async Task GPS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task GPS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [SkippableFact]
        public async Task GPS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task GPS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [SkippableFact]
        public async Task GPS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task GPS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [SkippableFact]
        public async Task GPS_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [SkippableFact]
        public async Task GPS_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [SkippableFact]
        public async Task GPS_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [SkippableFact]
        public async Task GPS_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [SkippableFact]
        public async Task GPS_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [SkippableFact]
        public async Task GPS_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(InternalClient, PUBSUB_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task GPS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(InternalClient, PUBSUB_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }
    }
}
