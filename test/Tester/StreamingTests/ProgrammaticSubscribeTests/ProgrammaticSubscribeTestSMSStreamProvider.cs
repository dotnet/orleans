using Orleans.Hosting;
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
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddSimpleMessageStreamProvider(StreamProviderName);
                hostBuilder.AddSimpleMessageStreamProvider(StreamProviderName2, options => options.PubSubType = StreamPubSubType.ExplicitGrainBasedOnly)
                    .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
            }
        }

        public ProgrammaticSubscribeTestSMSStreamProvider(ITestOutputHelper output, Fixture fixture)
            :base(fixture)
        {
        }
    }
}
