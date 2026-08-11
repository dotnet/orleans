using Orleans.Configuration;
using Orleans.Hosting.Developer;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceBus.Tests
{
    /// <summary>
    /// Tests for pluggable queue balancer functionality with EventHub streaming provider.
    /// </summary>
    [TestCategory("EventHub"), TestCategory("Streaming")]
    [TestSuite("BVT")]
    [TestProvider("EventHub")]
    [TestArea("Streaming")]
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
                                b.ConfigureComponent<IStreamQueueCheckpointerFactory>((s, n) => NoOpCheckpointerFactory.Instance);
                                b.ConfigurePartitionBalancing((s, n) => ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancerForTest>(s, n, TotalQueueCount / SiloCount));
                            });
                }
            }
        }

        public PluggableQueueBalancerTestsWithEHStreamProvider(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT")]
        public Task PluggableQueueBalancerTest_ShouldUseInjectedQueueBalancerAndBalanceCorrectly()
        {
            return base.ShouldUseInjectedQueueBalancerAndBalanceCorrectly(this.fixture, StreamProviderName, SiloCount, TotalQueueCount);
        }
    }
}
