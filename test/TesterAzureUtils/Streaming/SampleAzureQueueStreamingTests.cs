using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming")]
    public class SampleAzureQueueStreamingTests : TestClusterPerTest
    {
        private const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

        public override TestCluster CreateTestCluster()
        {
            TestUtils.CheckForAzureStorage();
            var options = new TestClusterOptions(2);

            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddAzureQueueStreamProvider(StreamProvider);
            return new TestCluster(options);
        }

        public override void Dispose()
        {
            var deploymentId = HostedCluster?.DeploymentId;
            if (deploymentId != null)
            {
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(StreamProvider, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SampleStreamingTests_4()
        {
            logger.Info("************************ SampleStreamingTests_4 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.HostedCluster);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SampleStreamingTests_5()
        {
            logger.Info("************************ SampleStreamingTests_5 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.HostedCluster);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }
    }
}
