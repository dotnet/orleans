using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.Hosting;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Configuration;
using Orleans;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    [TestCategory("Functional")]
    public class SubscriptionObserverWithImplicitSubscribingTestsUsingSMS : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<SubscriptionObserverWithImplicitSubscribingTestsUsingSMS.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
                builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
            }
        }

        private class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
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

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
        }
        
        public SubscriptionObserverWithImplicitSubscribingTestsUsingSMS(ITestOutputHelper output, Fixture fixture) : base(fixture)
        {
        }
    }
}
