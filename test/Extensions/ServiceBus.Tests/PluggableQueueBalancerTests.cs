using Orleans.Configuration;
using Orleans.Hosting.Developer;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
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

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddEventDataGeneratorStreams(
                            StreamProviderName,
                            b=>
                            {
                                b.Configure<EventDataGeneratorStreamOptions>(ob => ob.Configure(
                                options =>
                                {
                                    options.EventHubPartitionCount = TotalQueueCount;
                                }));
                                b.ConfigurePartitionBalancing((s, n) => ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancerForTest>(s, n));
                            });
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
