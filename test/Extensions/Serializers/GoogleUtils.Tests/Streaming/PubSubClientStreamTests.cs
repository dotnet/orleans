using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Hosting;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;

namespace GoogleUtils.Tests.Streaming
{
    [TestCategory("GCP"), TestCategory("PubSub")]
    public class PubSubClientStreamTests : TestClusterPerTest
    {
        private const string PROVIDER_NAME = "PubSubProvider";
        private const string STREAM_NAMESPACE = "PubSubSubscriptionMultiplicityTestsNamespace";

        private readonly ITestOutputHelper output;
        private ClientStreamTestRunner runner;

        public PubSubClientStreamTests(ITestOutputHelper output)
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
            if (!GoogleTestUtils.IsPubSubSimulatorAvailable.Value)
            {
                throw new SkipException("Google PubSub Simulator not available");
            }

            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddPubSubStreams<PubSubDataAdapter>(PROVIDER_NAME, options =>
                    {
                        options.ProjectId = GoogleTestUtils.ProjectId;
                        options.TopicId = GoogleTestUtils.TopicId;
                        options.Deadline = TimeSpan.FromSeconds(600);
                    })
                    .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddPubSubStreams<PubSubDataAdapter>(PROVIDER_NAME, options =>
                    {
                        options.ProjectId = GoogleTestUtils.ProjectId;
                        options.TopicId = GoogleTestUtils.TopicId;
                        options.Deadline = TimeSpan.FromSeconds(600);
                    });
            }
        }

        [SkippableFact]
        public async Task GPS_StreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(PROVIDER_NAME, STREAM_NAMESPACE);
        }

        [SkippableFact(Skip = "PubSub has unpredictable event delivery counts - re-enable when we figure out how to handle this.")]
        public async Task GPS_StreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(PROVIDER_NAME, STREAM_NAMESPACE, output);
        }
    }
}
