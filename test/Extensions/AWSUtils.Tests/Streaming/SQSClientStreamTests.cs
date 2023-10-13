using AWSUtils.Tests.StorageTests;
using Orleans.TestingHost;
using Microsoft.Extensions.Logging.Abstractions;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using OrleansAWSUtils.Streams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace AWSUtils.Tests.Streaming
{
    public class SQSClientStreamTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private readonly string StorageConnectionString = AWSTestConstants.SqsConnectionString;

        private readonly ITestOutputHelper output;
        private ClientStreamTestRunner runner;

        public SQSClientStreamTests(ITestOutputHelper output)
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
                    .AddSqsStreams(SQSStreamProviderName, options => 
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
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
                    .AddSqsStreams(SQSStreamProviderName, (Action<SqsOptions>)(options =>
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
                    }));
            }
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(StorageConnectionString))
            {
                await SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, clusterId, StorageConnectionString, NullLoggerFactory.Instance);
            }
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSStreamProducerOnDroppedClientTest()
        {
            logger.LogInformation("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SQSStreamProviderName, StreamNamespace);
        }
    }
}
