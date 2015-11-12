/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
                StartSecondary = true
            })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        public override void AdjustForTest(ClusterConfiguration config)
        {
            base.AdjustForTest(config);
            config.Globals.DefaultPlacementStrategy = "ActivationCountBasedPlacement";
            config.Globals.NumMissedProbesLimit = 1;
            config.Globals.NumVotesForDeathDeclaration = 1;
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
