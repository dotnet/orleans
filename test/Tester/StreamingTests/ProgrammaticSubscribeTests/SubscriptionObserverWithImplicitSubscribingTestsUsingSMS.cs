using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.Hosting;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    [TestCategory("Functional")]
    public class SubscriptionObserverWithImplicitSubscribingTestsUsingSMS : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<SubscriptionObserverWithImplicitSubscribingTestsUsingSMS.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddSimpleMessageStreamProvider(StreamProviderName,
                        options => options.PubSubType = StreamPubSubType.ImplicitOnly)
                        .AddSimpleMessageStreamProvider(StreamProviderName2,
                        options => options.PubSubType = StreamPubSubType.ImplicitOnly)
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }
        
        public SubscriptionObserverWithImplicitSubscribingTestsUsingSMS(ITestOutputHelper output, Fixture fixture)
            :base(fixture)
        {
        }
    }
}
