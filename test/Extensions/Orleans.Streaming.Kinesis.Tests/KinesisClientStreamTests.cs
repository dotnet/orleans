using Orleans.TestingHost;
using Microsoft.Extensions.Logging.Abstractions;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Orleans.Streaming.Kinesis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Streaming.Kinesis.Tests
{
    /// <summary>
    /// Tests Kinesis streaming functionality from Orleans client perspective including producer dropout scenarios.
    /// </summary>
    [TestSuite("Functional")]
    [TestArea("Streaming")]
    [TestProvider("Kinesis")]
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisClientStreamTests : TestClusterPerTest
    {
        private const string KinesisStreamProviderName = "KinesisProvider";
        private const string KinesisStreamName = "OrleansKinesisClientStreamTests";
        private const string StreamNamespace = "KinesisSubscriptionMultiplicityTestsNamespace";
        private readonly ITestOutputHelper output;
        private ClientStreamTestRunner runner = null!;
        private bool streamCreated;

        public KinesisClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public override async ValueTask InitializeAsync()
        {
            EnsurePreconditionsMet();
            await KinesisStreamTestResource.Create(KinesisStreamName);
            streamCreated = true;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        protected override void CheckPreconditionsOrThrow()
            => KinesisTestConstants.CheckPreconditionsOrThrow();

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName;
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
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName;
                    });
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                if (streamCreated)
                {
                    await KinesisStreamTestResource.Delete(KinesisStreamName);
                }
            }
        }

        [Fact]
        public async Task KinesisStreamProducerOnDroppedClientTest()
        {
            await runner.StreamProducerOnDroppedClientTest(KinesisStreamProviderName, StreamNamespace);
        }
    }
}