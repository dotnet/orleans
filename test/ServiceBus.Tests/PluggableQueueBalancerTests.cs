using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Orleans.Streams;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceBus.Tests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class PluggableQueueBalancerTestsWithEHStreamProvider : PluggableQueueBalancerTestBase, IClassFixture<PluggableQueueBalancerTestsWithEHStreamProvider.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private static readonly int TotalQueueCount = 6;
        private static readonly short SiloCount = 2;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = SiloCount;
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddPersistentStreams(StreamProviderName,
                            EventDataGeneratorAdapterFactory.Create, b=>b
                        .Configure<EventDataGeneratorStreamOptions>(ob => ob.Configure(
                            options =>
                            {
                                options.EventHubPartitionCount = TotalQueueCount;
                            }))
                         .ConfigurePartitionBalancing((s, n) => ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancerForTest>(s, n)));
                }
            }
        }

        public PluggableQueueBalancerTestsWithEHStreamProvider(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact(Skip = "https://github.com/dotnet/orleans/issues/4317"), TestCategory("BVT")]
        public Task PluggableQueueBalancerTest_ShouldUseInjectedQueueBalancerAndBalanceCorrectly()
        {
            return base.ShouldUseInjectedQueueBalancerAndBalanceCorrectly(this.fixture, StreamProviderName, SiloCount, TotalQueueCount);
        }
    }
}
