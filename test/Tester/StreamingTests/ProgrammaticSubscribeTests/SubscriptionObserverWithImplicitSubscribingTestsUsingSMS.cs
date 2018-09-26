using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit.Abstractions;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    public class SubscriptionObserverWithImplicitSubscribingTestsUsingSMS : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<SubscriptionObserverWithImplicitSubscribingTestsUsingSMS.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
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
