using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using Xunit;
using TestExtensions;
using UnitTests.Streaming;
using Orleans.Streaming.Kinesis;
using UnitTests.GrainInterfaces;

namespace Orleans.Streaming.Kinesis.Tests
{
    /// <summary>
    /// Tests Kinesis streaming provider with various producer/consumer configurations between grains and clients.
    /// </summary>
    [TestSuite("Functional")]
    [TestArea("Streaming")]
    [TestProvider("Kinesis")]
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisStreamTests : TestClusterPerTest
    {
        public static readonly string KINESIS_STREAM_PROVIDER_NAME = "KinesisProvider";
        private const string KinesisStreamName = "OrleansKinesisStreamTests";
        private const string KinesisStreamName2 = "OrleansKinesisStreamTests2";

        private SingleStreamTestRunner runner = null!;
        private bool streamCreated;
        private bool stream2Created;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        protected override void CheckPreconditionsOrThrow()
        {
            KinesisTestConstants.CheckPreconditionsOrThrow();
            KinesisTestConstants.CheckDynamoDbPreconditionsOrThrow();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddKinesisStreams("KinesisProvider", stream =>
                    {
                        stream.ConfigureKinesis(options =>
                        {
                            options.ConnectionString = KinesisTestConstants.ConnectionString;
                            options.StreamName = KinesisStreamName;
                        });
                        stream.UseDynamoDBCheckpointer(options =>
                        {
                            options.Service = KinesisTestConstants.DynamoDbService;
                            options.SecretKey = KinesisTestConstants.DynamoDbSecretKey;
                            options.AccessKey = KinesisTestConstants.DynamoDbAccessKey;
                        });
                    })
                    .AddKinesisStreams("KinesisProvider2", options =>
                    {
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName2;
                    })
                    .AddDynamoDBGrainStorage("DynamoDBStore", options =>
                    {
                        options.Service = KinesisTestConstants.DynamoDbService;
                        options.SecretKey = KinesisTestConstants.DynamoDbSecretKey;
                        options.AccessKey = KinesisTestConstants.DynamoDbAccessKey;
                        options.DeleteStateOnClear = true;
                    })
                    .AddDynamoDBGrainStorage("PubSubStore", options =>
                    {
                        options.Service = KinesisTestConstants.DynamoDbService;
                        options.SecretKey = KinesisTestConstants.DynamoDbSecretKey;
                        options.AccessKey = KinesisTestConstants.DynamoDbAccessKey;
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
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName;
                    });
            }
        }

        public override async ValueTask InitializeAsync()
        {
            EnsurePreconditionsMet();
            await KinesisStreamTestResource.Create(KinesisStreamName, TestContext.Current.CancellationToken);
            streamCreated = true;
            await KinesisStreamTestResource.Create(KinesisStreamName2, TestContext.Current.CancellationToken);
            stream2Created = true;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            runner = new SingleStreamTestRunner(this.HostedCluster, KINESIS_STREAM_PROVIDER_NAME);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (streamCreated)
                    {
                        await KinesisStreamTestResource.DeleteForCleanup(
                            KinesisStreamName,
                            TestContext.Current.CancellationToken);
                    }
                }
                finally
                {
                    if (stream2Created)
                    {
                        await KinesisStreamTestResource.DeleteForCleanup(
                            KinesisStreamName2,
                            TestContext.Current.CancellationToken);
                    }
                }
            }
        }

        ////------------------------ One to One ----------------------//

        [Fact]
        public async Task Kinesis_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient(TestContext.Current.CancellationToken);
        }

        //------------------------- MANY to Many different grains ----------------------//

        [Fact]
        public async Task Kinesis_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients(TestContext.Current.CancellationToken);
        }

        //------------------------- MANY to Many Same grains ----------------------//
        [Fact]
        public async Task Kinesis_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact]
        public async Task Kinesis_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false, TestContext.Current.CancellationToken);
        }

        //----------------------------------------------//

        [Fact]
        public async Task Kinesis_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.HostedCluster, KINESIS_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                cancellationToken: TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Kinesis_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.HostedCluster, KINESIS_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                () => HostedCluster.StartAdditionalSilo(),
                cancellationToken: TestContext.Current.CancellationToken);
        }


    }
}
