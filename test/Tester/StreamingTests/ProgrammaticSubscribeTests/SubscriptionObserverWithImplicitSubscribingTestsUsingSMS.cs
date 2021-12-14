using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.Hosting;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;

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

        [SkippableFact]
        public override async Task StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager()
        {
            await base.StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            await base.StreamingTests_Consumer_Producer_Subscribe();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
        {
            await base.StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism();
        }

        [SkippableFact]
        public override async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
        {
            await base.StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider();
        }
    }
}
