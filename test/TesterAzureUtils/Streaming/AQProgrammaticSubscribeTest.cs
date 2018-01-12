using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("AQStreaming")]
    public class AQProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<AQProgrammaticSubscribeTest.Fixture>
    {
        private Fixture fixture;
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                options.ClusterConfiguration.AddAzureQueueStreamProviderV2(StreamProviderName);
                options.ClusterConfiguration.AddAzureQueueStreamProviderV2(StreamProviderName2);
                return new TestCluster(options);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            var clusterId = this.fixture.HostedCluster.ClusterId;
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, StreamProviderName, clusterId, TestDefaultConfiguration.DataConnectionString).Wait();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, StreamProviderName2, clusterId, TestDefaultConfiguration.DataConnectionString).Wait();
        }

        public AQProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
        }
    }

}
