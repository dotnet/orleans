using AWSUtils.Tests.StorageTests;
using Orleans.TestingHost;
using Microsoft.Extensions.Logging.Abstractions;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Streaming.Kinesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace AWSUtils.Tests.Streaming
{
    /// <summary>
    /// Tests Kinesis streaming functionality from Orleans client perspective including producer dropout scenarios.
    /// </summary>
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisClientStreamTests : TestClusterPerTest
    {
        private const string KinesisStreamProviderName = "KinesisProvider";
        private const string StreamNamespace = "KinesisSubscriptionMultiplicityTestsNamespace";
        private readonly string StorageConnectionString = AWSTestConstants.KinesisConnectionString;

        private readonly ITestOutputHelper output;
        private ClientStreamTestRunner runner;

        public KinesisClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

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
                    .AddKinesisStreams(KinesisStreamProviderName, options => 
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    })
                    .AddMemoryGrainStorage("PubSubStore")
                    .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    });
            }
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            // TODO: Add cleanup logic for Kinesis streams if needed
        }

        [SkippableFact]
        public async Task Kinesis_Orleans_9730_OneProducerOneConsumerClient()
        {
            await runner.StreamTest_09_ConsumerReceivesFromProducerClient(KinesisStreamProviderName);
        }

        [SkippableFact]
        public async Task Kinesis_Orleans_9730_OneProducerOneConsumerGrain()
        {
            await runner.StreamTest_09_ConsumerReceivesFromProducerGrain(KinesisStreamProviderName);
        }

        [SkippableFact]
        public async Task KinesisStreamProducerOnDroppedClientTest()
        {
            var producerClientId = Guid.NewGuid();
            await runner.StreamProducerOnDroppedClientTest(KinesisStreamProviderName, producerClientId);
        }
    }
}