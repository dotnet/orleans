using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;

namespace Tester.StreamingTests
{
    public class PluggableQueueBalancerTestBase : OrleansTestingBase
    {
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly Type QueueBalancerType = typeof(LeaseBasedQueueBalancerForTest);

        public virtual async Task ShouldUseInjectedQueueBalancerAndBalanceCorrectly(BaseTestClusterFixture fixture, string streamProviderName, int siloCount, int totalQueueCount)
        {
            var leaseManager = fixture.GrainFactory.GetGrain<ILeaseManagerGrain>(streamProviderName);
            var expectedResponsibilityPerBalancer = totalQueueCount / siloCount;
            await TestingUtils.WaitUntilAsync(lastTry => CheckLeases(leaseManager, siloCount, expectedResponsibilityPerBalancer, lastTry), Timeout);
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
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
