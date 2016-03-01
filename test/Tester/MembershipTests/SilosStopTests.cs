using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.MembershipTests
{
    public class SilosStopTests : HostedTestClusterPerTest
    {
        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                AdjustConfig = config => {
                    config.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
                    config.Globals.NumMissedProbesLimit = 1;
                    config.Globals.NumVotesForDeathDeclaration = 1;
                }
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            Assert.AreNotEqual(instanceId, targetInstanceId, "Activations must be placed on different silos");
            var promise = instanceId.Contains(HostedCluster.Primary.Endpoint.ToString()) ?
                grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7))
                : target.CallOtherLongRunningTask(grain, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            HostedCluster.KillSilo(HostedCluster.Secondary);
            try
            {
                await promise;
                Assert.Fail("The broken promise exception was not thrown");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(typeof(SiloUnavailableException), ex.GetBaseException().GetType());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_ClientOutstandingRequestsBreak()
        {
            var grain = HostedCluster.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var task = grain.LongRunningTask(true, TimeSpan.FromSeconds(7));
            await Task.Delay(500);

            HostedCluster.KillSilo(HostedCluster.Secondary);
            HostedCluster.KillSilo(HostedCluster.Primary);
            try
            {
                await task;
                Assert.Fail("The broken promise exception was not thrown");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(typeof(SiloUnavailableException), ex.GetBaseException().GetType());
            }
        }
        
    }
}
