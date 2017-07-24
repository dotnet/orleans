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

        public override TestCluster CreateTestCluster()
        {
            if (!GoogleTestUtils.IsPubSubSimulatorAvailable.Value)
            {
                throw new SkipException("Google PubSub Simulator not available");
            }

            var providerSettings = new Dictionary<string, string>
                {
                    { "ProjectId",  GoogleTestUtils.ProjectId },
                    { "TopicId",  GoogleTestUtils.TopicId },
                    { "DeploymentId",  GoogleTestUtils.DeploymentId.ToString()},
                    { "Deadline",  "600" },
                    //{ "CustomEndpoint", "localhost:8085" }
                };

            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.Globals.RegisterStreamProvider<PubSubStreamProvider>(PROVIDER_NAME, providerSettings);
            options.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            options.ClientConfiguration.RegisterStreamProvider<PubSubStreamProvider>(PROVIDER_NAME, providerSettings);
            return new TestCluster(options);
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
