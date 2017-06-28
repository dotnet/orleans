using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.StreamingTests.PlugableQueueBalancerTests
{
    public class PluggableQueueBalancerTestsWithMemoryStreamProvider : PluggableQueueBalancerTestBase, IClassFixture<PluggableQueueBalancerTestsWithMemoryStreamProvider.Fixture>
    {
        private const string StreamProviderName = "MemoryStreamProvider";
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 2;
        public static readonly MemoryAdapterConfig ProviderSettings =
            new MemoryAdapterConfig(StreamProviderName);

        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(siloCount);
                ProviderSettings.TotalQueueCount = totalQueueCount;
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                ConfigureCustomQueueBalancer(settings, config);

                // register stream provider
                config.Globals.RegisterStreamProvider<MemoryStreamProvider>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        public PluggableQueueBalancerTestsWithMemoryStreamProvider(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT")]
        public Task PluggableQueueBalancerTest_ShouldUseInjectedQueueBalancerAndBalanceCorrectly()
        {
            return base.PluggableQueueBalancerTest_ShouldUseInjectedQueueBalancerAndBalanceCorrectly(this.fixture, StreamProviderName, siloCount, totalQueueCount);
        }
    }
}
