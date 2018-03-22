using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using System.Linq;
using System.Collections.Generic;

namespace Tester.StreamingTests
{
    public class PluggableQueueBalancerTestBase : OrleansTestingBase
    {
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static Type QueueBalancerType = typeof(LeaseBasedQueueBalancerForTest);

        public virtual async Task ShouldUseInjectedQueueBalancerAndBalanceCorrectly(BaseTestClusterFixture fixture, string streamProviderName, int siloCount, int totalQueueCount)
        {
            var leaseManager = fixture.GrainFactory.GetGrain<ILeaseManagerGrain>(streamProviderName);
            var expectedResponsibilityPerBalancer = totalQueueCount / siloCount;
            await TestingUtils.WaitUntilAsync(lastTry => CheckLeases(leaseManager, siloCount, expectedResponsibilityPerBalancer, lastTry), Timeout);
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(services => services.AddTransient<LeaseBasedQueueBalancerForTest>());
            }
        }

        private async Task<bool> CheckLeases(ILeaseManagerGrain leaseManager, int siloCount, int expectedResponsibilityPerBalancer, bool lastTry)
        {
            Dictionary<string,int> responsibilityMap = await leaseManager.GetResponsibilityMap();
            if(lastTry)
            {
                //there should be one StreamQueueBalancer per silo
                Assert.Equal(responsibilityMap.Count, siloCount);
                foreach (int responsibility in responsibilityMap.Values)
                {
                    Assert.Equal(expectedResponsibilityPerBalancer, responsibility);
                }
            }
            return (responsibilityMap.Count == siloCount)
                && (responsibilityMap.Values.All(responsibility => expectedResponsibilityPerBalancer == responsibility));
        }
    }
}
