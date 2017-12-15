using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
    public class StreamFilteringTests_AQ : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_AQ.Fixture>, IDisposable
    {
        private readonly string clusterId;
        public class Fixture : BaseAzureTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                    legacy.ClusterConfiguration.AddAzureQueueStreamProvider(StreamProvider);
                });
            }

            public override void Dispose()
            {
                var clusterId = this.HostedCluster?.ClusterId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, StreamProvider, clusterId, TestDefaultConfiguration.DataConnectionString)
                    .Wait();
            }
        }

        public StreamFilteringTests_AQ(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.clusterId = fixture.HostedCluster.ClusterId;
            streamProviderName = Fixture.StreamProvider;
        }

        public virtual void Dispose()
        {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(NullLoggerFactory.Instance, 
                    streamProviderName,
                    this.clusterId,
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
            await Assert.ThrowsAsync<ArgumentException>(() =>
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
