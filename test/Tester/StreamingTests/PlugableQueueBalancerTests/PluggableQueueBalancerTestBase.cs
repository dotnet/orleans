using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.StreamingTests
{
    public class PluggableQueueBalancerTestBase : OrleansTestingBase
    {
        private static Type QueueBalancerType = typeof(LeaseBasedQueueBalancerForTest);
        private static PersistentStreamProviderConfig CustomPersistentProviderConfig = CreateConfigWithCustomBalancerType();

        public static void ConfigureCustomQueueBalancer(Dictionary<string, string> streamProviderSettings, ClusterConfiguration config)
        {
            CustomPersistentProviderConfig.WriteProperties(streamProviderSettings);
        }

        private static PersistentStreamProviderConfig CreateConfigWithCustomBalancerType()
        {
            var config = new PersistentStreamProviderConfig();
            config.BalancerType = QueueBalancerType;
            return config;
        }

        public virtual async Task ShouldUseInjectedQueueBalancerAndBalanceCorrectly(BaseTestClusterFixture fixture, string streamProviderName, int siloCount, int totalQueueCount)
        {
            var leaseManager = fixture.GrainFactory.GetGrain<ILeaseManagerGrain>(streamProviderName);
            var responsibilityMap = await leaseManager.GetResponsibilityMap();
            //there should be one StreamQueueBalancer per silo
            Assert.Equal(responsibilityMap.Count, siloCount);
            var expectedResponsibilityPerBalancer = totalQueueCount / siloCount;
            foreach (var responsibility in responsibilityMap)
            {
                Assert.Equal(expectedResponsibilityPerBalancer, responsibility.Value);
            }
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services => services.AddTransient<LeaseBasedQueueBalancerForTest>());
            }
        }
    }
}
