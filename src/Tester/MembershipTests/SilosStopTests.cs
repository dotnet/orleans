using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.MembershipTests
{
    [TestClass]
    public class SilosStopTests : UnitTestSiloHost
    {
        public SilosStopTests()
            : base(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartPrimary = true,
                StartSecondary = true,
                AdjustConfig = config => {
                    config.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
                    config.Globals.NumMissedProbesLimit = 1;
                    config.Globals.NumVotesForDeathDeclaration = 1;
                }
            })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var instanceId = await grain.GetRuntimeInstanceId();
            var target = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
            var targetInstanceId = await target.GetRuntimeInstanceId();
            Assert.AreNotEqual(instanceId, targetInstanceId, "Activations must be placed on different silos");
            var promise = instanceId.Contains(Primary.Endpoint.ToString()) ?
                grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7))
                : target.CallOtherLongRunningTask(grain, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500);
            KillSilo(Secondary);
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

            KillSilo(Secondary);
            KillSilo(Primary);
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
