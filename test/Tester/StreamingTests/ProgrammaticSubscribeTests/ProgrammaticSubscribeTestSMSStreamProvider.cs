using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Functional")]
    public class ProgrammaticSubscribeTestSMSStreamProvider : ProgrammaticSubcribeTestsRunner, IClassFixture<ProgrammaticSubscribeTestSMSStreamProvider.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName, false, true,
                    StreamPubSubType.ExplicitGrainBasedAndImplicit);
                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName2, false, true,
                    StreamPubSubType.ExplicitGrainBasedOnly);
                return new TestCluster(options);
            }
        }

        public ProgrammaticSubscribeTestSMSStreamProvider(ITestOutputHelper output, Fixture fixture)
            :base(fixture)
        {
        }
    }
}
