using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using Xunit;
using TestExtensions;
using UnitTests.Streaming;
using Orleans.Streaming.Kinesis;

namespace AWSUtils.Tests.Streaming
{
    /// <summary>
    /// Tests Kinesis streaming provider with various producer/consumer configurations between grains and clients.
    /// </summary>
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisStreamTests : TestClusterPerTest
    {
        public static readonly string KINESIS_STREAM_PROVIDER_NAME = "KinesisProvider";

        private SingleStreamTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsKinesisAvailable)
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
                    .AddKinesisStreams("KinesisProvider", options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    })
                    .AddKinesisStreams("KinesisProvider2", options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
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
                    .AddMemoryGrainStorage("MemoryStore", op => op.NumStorageGrains = 1);
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddKinesisStreams("KinesisProvider", options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    });
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new SingleStreamTestRunner(this.InternalClient, KINESIS_STREAM_PROVIDER_NAME);
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            // TODO: Add cleanup logic for Kinesis streams if needed
        }

        ////------------------------ One to One ----------------------//

        [SkippableFact]
        public async Task Kinesis_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [SkippableFact]
        public async Task Kinesis_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [SkippableFact]
        public async Task Kinesis_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [SkippableFact]
        public async Task Kinesis_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------- MANY to Many different grains ----------------------//

        [SkippableFact]
        public async Task Kinesis_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [SkippableFact]
        public async Task Kinesis_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------- MANY to Many Same grains ----------------------//
        [SkippableFact]
        public async Task Kinesis_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [SkippableFact]
        public async Task Kinesis_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------- MANY to One ----------------------//

        [SkippableFact]
        public async Task Kinesis_13_ManyToOne_ManyProducerGrainsOneConsumerGrain()
        {
            await runner.StreamTest_13_ManyToOne_ManyProducerGrainsOneConsumerGrain();
        }

        [SkippableFact]
        public async Task Kinesis_14_ManyToOne_ManyProducerGrainsOneConsumerClient()
        {
            await runner.StreamTest_14_ManyToOne_ManyProducerGrainsOneConsumerClient();
        }

        [SkippableFact]
        public async Task Kinesis_15_ManyToOne_ManyProducerClientsOneConsumerGrain()
        {
            await runner.StreamTest_15_ManyToOne_ManyProducerClientsOneConsumerGrain();
        }

        [SkippableFact]
        public async Task Kinesis_16_ManyToOne_ManyProducerClientsOneConsumerClient()
        {
            await runner.StreamTest_16_ManyToOne_ManyProducerClientsOneConsumerClient();
        }

        //------------------------- One to MANY ----------------------//

        [SkippableFact]
        public async Task Kinesis_17_OneToMany_OneProducerGrainManyConsumerGrains()
        {
            await runner.StreamTest_17_OneToMany_OneProducerGrainManyConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_18_OneToMany_OneProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_18_OneToMany_OneProducerGrainManyConsumerClients();
        }

        [SkippableFact]
        public async Task Kinesis_19_OneToMany_OneProducerClientManyConsumerGrains()
        {
            await runner.StreamTest_19_OneToMany_OneProducerClientManyConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_20_OneToMany_OneProducerClientManyConsumerClients()
        {
            await runner.StreamTest_20_OneToMany_OneProducerClientManyConsumerClients();
        }

        //------------------------- Broadcast ----------------------//

        [SkippableFact]
        public async Task Kinesis_21_Broadcast_ProducerGrainAllConsumerGrains()
        {
            await runner.StreamTest_21_Broadcast_ProducerGrainAllConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_22_Broadcast_ProducerGrainAllConsumerClients()
        {
            await runner.StreamTest_22_Broadcast_ProducerGrainAllConsumerClients();
        }

        [SkippableFact]
        public async Task Kinesis_23_Broadcast_ProducerClientAllConsumerGrains()
        {
            await runner.StreamTest_23_Broadcast_ProducerClientAllConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_24_Broadcast_ProducerClientAllConsumerClients()
        {
            await runner.StreamTest_24_Broadcast_ProducerClientAllConsumerClients();
        }

        //------------------------- Broadcast into MANY ----------------------//

        [SkippableFact]
        public async Task Kinesis_25_Broadcast_ManyProducerGrainsAllConsumerGrains()
        {
            await runner.StreamTest_25_Broadcast_ManyProducerGrainsAllConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_26_Broadcast_ManyProducerGrainsAllConsumerClients()
        {
            await runner.StreamTest_26_Broadcast_ManyProducerGrainsAllConsumerClients();
        }

        [SkippableFact]
        public async Task Kinesis_27_Broadcast_ManyProducerClientsAllConsumerGrains()
        {
            await runner.StreamTest_27_Broadcast_ManyProducerClientsAllConsumerGrains();
        }

        [SkippableFact]
        public async Task Kinesis_28_Broadcast_ManyProducerClientsAllConsumerClients()
        {
            await runner.StreamTest_28_Broadcast_ManyProducerClientsAllConsumerClients();
        }
    }
}