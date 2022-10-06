using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using Xunit;
using TestExtensions;
using UnitTests.Streaming;
using OrleansAWSUtils.Streams;
using Orleans;

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
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddSqsStreams("SQSProvider", options =>
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
                    })
                    .AddSqsStreams("SQSProvider2", options =>
                     {
                         options.ConnectionString = AWSTestConstants.SqsConnectionString;
                     })
                    .AddDynamoDBGrainStorage("DynamoDBStore", options =>
                    {
                        options.Service = AWSTestConstants.DynamoDbService;
                        options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                        options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                        options.DeleteStateOnClear = true;
                    })
                    .AddDynamoDBGrainStorage("PubSubStore", options =>
                    {
                        options.Service = AWSTestConstants.DynamoDbService;
                        options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                        options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                    })
                    .AddMemoryGrainStorage("MemoryStore", op=>op.NumStorageGrains = 1);
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddSqsStreams("SQSProvider", (System.Action<Orleans.Configuration.SqsOptions>)(options =>
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
                    }));
            }
        }
        
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new SingleStreamTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME);
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(AWSTestConstants.SqsConnectionString))
            {
                SQSStreamProviderUtils.DeleteAllUsedQueues(SQS_STREAM_PROVIDER_NAME, clusterId, AWSTestConstants.SqsConnectionString, NullLoggerFactory.Instance).Wait();
            }
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
