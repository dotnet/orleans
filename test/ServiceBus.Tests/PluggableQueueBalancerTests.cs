using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class PluggableQueueBalancerTestsWithEHStreamProvider : PluggableQueueBalancerTestBase, IClassFixture<PluggableQueueBalancerTestsWithEHStreamProvider.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 2;
        public static readonly EventHubGeneratorStreamProviderSettings ProviderSettings =
            new EventHubGeneratorStreamProviderSettings(StreamProviderName);

        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(siloCount);
                ProviderSettings.EventHubPartitionCount = totalQueueCount;
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                ProviderSettings.WriteDataGeneratingConfig(settings);
                ConfigureCustomQueueBalancer(settings, config);

                // register stream provider
                config.Globals.RegisterStreamProvider<EventDataGeneratorStreamProvider>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        public PluggableQueueBalancerTestsWithEHStreamProvider(Fixture fixture)
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
