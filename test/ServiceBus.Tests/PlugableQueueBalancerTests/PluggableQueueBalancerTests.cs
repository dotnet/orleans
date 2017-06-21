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
using TestExtensions;
using Xunit;

namespace Tester.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class PluggableQueueBalancerTests : OrleansTestingBase, IClassFixture<PluggableQueueBalancerTests.Fixture>
    {
        private static string QueueBalancerFactoryName = "CustomBalancerFactory";
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 2;
        public static readonly EventHubGeneratorStreamProviderSettings ProviderSettings =
            new EventHubGeneratorStreamProviderSettings(StreamProviderName);
        public static readonly PersistentStreamProviderConfig PersistentProviderConfig =
            new PersistentStreamProviderConfig();

        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(siloCount);
                ProviderSettings.EventHubPartitionCount = totalQueueCount;
                //configure custom queue balancer
                PersistentProviderConfig.QueueBalancerFactoryName = QueueBalancerFactoryName;
                PersistentProviderConfig.BalancerType = StreamQueueBalancerType.CustomBalancer;
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                ProviderSettings.WriteDataGeneratingConfig(settings);
                PersistentProviderConfig.WriteProperties(settings);

                // register stream provider
                config.Globals.RegisterStreamProvider<EventDataGeneratorStreamProvider>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
                config.UseStartupType<TestStartup>();
            }
        }

        public PluggableQueueBalancerTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
        }

        [Fact, TestCategory("BVT")]
        public async Task PluggableQueueBalancerTest_ShouldUseInjectedQueueBalancerAndBalanceCorrectly()
        {
            var leaseManager = this.fixture.GrainFactory.GetGrain<ILeaseManagerGrain>(StreamProviderName);
            var responsibilityMap = await leaseManager.GetResponsibilityMap();
            //there should be one StreamQueueBalancer per silo
            Assert.Equal(responsibilityMap.Count, siloCount);
            var expectedResponsibilityPerBalancer = totalQueueCount / siloCount;
            foreach (var responsibility in responsibilityMap)
            {
                Assert.Equal(expectedResponsibilityPerBalancer, responsibility.Value);
            }
        }

        public class TestStartup
        {
            public IServiceProvider ConfigureServices(IServiceCollection services)
            {
                var keyedFactories = new KeyedQueueBalancerFactoryCollection();
                keyedFactories.AddService(QueueBalancerFactoryName, new QueueBalancerFactory());
                services.AddSingleton<IKeyedServiceCollection<string, IStreamQueueBalancerFactory>>(keyedFactories);
                //ad an empty bag for keyed StreamQueueMapper for later use
                services.AddSingleton<IKeyedServiceCollection<string, IStreamQueueMapper>>(new KeyedStreamQueueMapperCollection());
                return services.BuildServiceProvider();
            }
        }
    }
}
