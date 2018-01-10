using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("AQStreaming")]
    public class ProgrammaticSubscribeTestAQStreamProvider : ProgrammaticSubcribeTestsRunner, IClassFixture<ProgrammaticSubscribeTestAQStreamProvider.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
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

        public ProgrammaticSubscribeTestAQStreamProvider(ITestOutputHelper output, Fixture fixture)
            : base(fixture.HostedCluster)
        {
        }
    }

}
