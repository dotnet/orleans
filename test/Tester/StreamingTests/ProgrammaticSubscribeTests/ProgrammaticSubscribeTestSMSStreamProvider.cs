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
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName,
                        false,
                        true,
                        StreamPubSubType.ExplicitGrainBasedAndImplicit);
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName2,
                        false,
                        true,
                        StreamPubSubType.ExplicitGrainBasedOnly);
                });
            }
        }

        public ProgrammaticSubscribeTestSMSStreamProvider(ITestOutputHelper output, Fixture fixture)
            :base(fixture)
        {
        }
    }
}
