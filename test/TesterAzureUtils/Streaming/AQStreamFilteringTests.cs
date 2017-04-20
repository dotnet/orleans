using System;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
    public class StreamFilteringTests_AQ : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_AQ.Fixture>, IDisposable
    {
        private readonly string deploymentId;

        public class Fixture : BaseAzureTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddAzureQueueStreamProvider(StreamProvider);
                return new TestCluster(options);
            }

            public override void Dispose()
            {
                var deploymentId = this.HostedCluster?.DeploymentId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(StreamProvider, deploymentId, TestDefaultConfiguration.DataConnectionString)
                    .Wait();
            }
        }

        public StreamFilteringTests_AQ(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.deploymentId = fixture.HostedCluster.DeploymentId;
            streamProviderName = Fixture.StreamProvider;
        }

        public virtual void Dispose()
        {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(
                    streamProviderName,
                    this.deploymentId,
                    TestDefaultConfiguration.DataConnectionString).Wait();
            }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_BadFunc()
        {
            await Assert.ThrowsAsync(typeof(ArgumentException), () =>
                Test_Filter_BadFunc());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }
}
