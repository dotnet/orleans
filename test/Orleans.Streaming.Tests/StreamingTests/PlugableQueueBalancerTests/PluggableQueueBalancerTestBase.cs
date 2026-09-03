using Xunit;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;

namespace Tester.StreamingTests
{
    public class PluggableQueueBalancerTestBase : OrleansTestingBase
    {
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        public virtual async Task ShouldUseInjectedQueueBalancerAndBalanceCorrectly(
            BaseTestClusterFixture fixture,
            string streamProviderName,
            int siloCount,
            int totalQueueCount,
            CancellationToken cancellationToken = default)
        {
            var leaseManager = fixture.GrainFactory.GetGrain<ILeaseManagerGrain>(streamProviderName);
            var expectedResponsibilityPerBalancer = totalQueueCount / siloCount;
            await TestingUtils.WaitUntilAsync(
                (lastTry, token) => CheckLeases(leaseManager, siloCount, expectedResponsibilityPerBalancer, lastTry, token),
                Timeout,
                cancellationToken: cancellationToken);
        }

        private async Task<bool> CheckLeases(ILeaseManagerGrain leaseManager, int siloCount, int expectedResponsibilityPerBalancer, bool lastTry, CancellationToken cancellationToken)
        {
            Dictionary<string, int> responsibilityMap = await leaseManager.GetResponsibilityMap(cancellationToken);
            if (lastTry)
            {
                //there should be one StreamQueueBalancer per silo
                Assert.Equal(siloCount, responsibilityMap.Count);
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
