using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

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
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    AdjustClusterConfiguration(legacy.ClusterConfiguration);
                });
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                // register stream provider
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams<EventDataGeneratorStreamOptions>(StreamProviderName,
                            EventDataGeneratorAdapterFactory.Create,
                            options =>
                            {
                                options.EventHubPartitionCount = TotalQueueCount;
                                options.BalancerType = typeof(LeaseBasedQueueBalancerForTest);
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
