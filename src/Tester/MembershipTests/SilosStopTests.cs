using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.MembershipTests
{
    [TestClass]
    public class SilosStopTests : HostedTestClusterPerTest
    {
        public static TestingSiloHost CreateSiloHost()
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

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            Assert.AreNotEqual(instanceId, targetInstanceId, "Activations must be placed on different silos");
            var promise = instanceId.Contains(this.HostedCluster.Primary.Endpoint.ToString()) ?
                grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7))
                : target.CallOtherLongRunningTask(grain, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            this.HostedCluster.KillSilo(this.HostedCluster.Secondary);
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

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_ClientOutstandingRequestsBreak()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var task = grain.LongRunningTask(true, TimeSpan.FromSeconds(7));
            await Task.Delay(500);

            this.HostedCluster.KillSilo(this.HostedCluster.Secondary);
            this.HostedCluster.KillSilo(this.HostedCluster.Primary);
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
