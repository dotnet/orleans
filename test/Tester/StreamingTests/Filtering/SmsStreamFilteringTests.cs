using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.StreamingTests.Filtering
{
    public class SmsStreamFilteringTests : StreamFilteringTestsBase, IClassFixture<SmsStreamFilteringTests.Fixture>
    {
        public SmsStreamFilteringTests(Fixture fixture) : base(fixture)
        {
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME)
                        .AddStreamFilter<CustomStreamFilter>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME)
                        .AddMemoryGrainStorage("MemoryStore")
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            public class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME)
                        .AddStreamFilter<CustomStreamFilter>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
                }
            }
        }

        protected override string ProviderName => StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task IgnoreBadFilter() => await base.IgnoreBadFilter();

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task OnlyEvenItems() => await base.OnlyEvenItems();

        [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task MultipleSubscriptionsDifferentFilterData() => await base.MultipleSubscriptionsDifferentFilterData();
    }
}
