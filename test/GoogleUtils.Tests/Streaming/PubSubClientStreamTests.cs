using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Hosting;
using Microsoft.Extensions.Configuration;
using Orleans;

namespace GoogleUtils.Tests.Streaming
{
    [TestCategory("GCP"), TestCategory("PubSub")]
    public class PubSubClientStreamTests : TestClusterPerTest
    {
        private const string PROVIDER_NAME = "PubSubProvider";
        private const string STREAM_NAMESPACE = "PubSubSubscriptionMultiplicityTestsNamespace";

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public PubSubClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!GoogleTestUtils.IsPubSubSimulatorAvailable.Value)
            {
                throw new SkipException("Google PubSub Simulator not available");
            }

            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddPubSubStreams<PubSubDataAdapter>(PROVIDER_NAME, options =>
                    {
                        options.ProjectId = GoogleTestUtils.ProjectId;
                        options.TopicId = GoogleTestUtils.TopicId;
                        options.Deadline = TimeSpan.FromSeconds(600);
                    });
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
