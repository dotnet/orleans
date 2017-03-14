using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    public class StreamFilteringTests_AQ : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_AQ.Fixture>, IDisposable
    {
        private readonly string deploymentId;

        public class Fixture : BaseTestClusterFixture
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
                var deploymentId = this.HostedCluster.DeploymentId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(StreamProvider, deploymentId, TestDefaultConfiguration.DataConnectionString)
                    .Wait();
            }
        }

        public StreamFilteringTests_AQ(Fixture fixture) : base(fixture)
        {
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

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_BadFunc()
        {
            await Assert.ThrowsAsync(typeof(ArgumentException), () =>
                Test_Filter_BadFunc());
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }
}
