using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace AWSUtils.Tests.Streaming
{
    /// <summary>
    /// Tests SQS streaming provider with various producer/consumer configurations between grains and clients.
    /// </summary>
    [TestCategory("AWS"), TestCategory("SQS")]
    [TestSuite("Functional")]
    [TestProvider("SQS")]
    [TestArea("Streaming")]
    public class SQSStreamTests : TestClusterPerTest
    {
        public static readonly string SQS_STREAM_PROVIDER_NAME = "SQSProvider";

        private SingleStreamTestRunner runner = null!;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw Xunit.Sdk.SkipException.ForSkip("Empty connection string");
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
                    .AddMemoryGrainStorage("MemoryStore", op=>op.NumStorageGrains = 1);

                if (!string.IsNullOrEmpty(AWSTestConstants.DynamoDbService))
                {
                    hostBuilder
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
                        });
                }
                else
                {
                    hostBuilder
                        .AddMemoryGrainStorage("DynamoDBStore")
                        .AddMemoryGrainStorage("PubSubStore");
                }
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
        
        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            runner = new SingleStreamTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!PreconditionsMet)
            {
                return;
            }

            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(AWSTestConstants.SqsConnectionString))
            {
                await Task.WhenAll(
                    SQSStreamProviderUtils.DeleteAllUsedQueues(
                        SQS_STREAM_PROVIDER_NAME,
                        clusterId,
                        AWSTestConstants.SqsConnectionString,
                        NullLoggerFactory.Instance),
                    SQSStreamProviderUtils.DeleteAllUsedQueues(
                        "SQSProvider2",
                        clusterId,
                        AWSTestConstants.SqsConnectionString,
                        NullLoggerFactory.Instance));
            }
        }

        ////------------------------ One to One ----------------------//

        [Fact]
        public async Task SQS_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient(TestContext.Current.CancellationToken);
        }

        //------------------------ MANY to Many different grains ----------------------//

        [Fact]
        public async Task SQS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients(TestContext.Current.CancellationToken);
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [Fact]
        public async Task SQS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact]
        public async Task SQS_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false, TestContext.Current.CancellationToken);
        }

        //----------------------------------------------//

        [Fact]
        public async Task SQS_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest(TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                cancellationToken: TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SQS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SQS_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                () => HostedCluster.StartAdditionalSilo(),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }
}
