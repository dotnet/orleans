using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.SchedulerTests
{
    [TestCategory("BVT")]
    public class STSchedulerLongTurnTest : HostedTestClusterEnsureDefaultStarted
    {
        public STSchedulerLongTurnTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_LongTurnTest()
        {
            // With two silos, there should be 16 threads.
            // We'll create way more grains than that to make sure we swamp the thread pools
            var grains = new List<IErrorGrain>();
            var grainFullName = typeof(ErrorGrain).FullName;
            for (int i = 0; i < 100; i++)
            {
                grains.Add(this.GrainFactory.GetGrain<IErrorGrain>(GetRandomGrainId(), grainFullName));
            }

            // Send a bunch of do-nothing requests just to get the grains activated
            var promises = grains.Select(grain => grain.Dispose());
            Task.WhenAll(promises).Wait();


            // Now start a timer, and then queue up a bunch of long (sleeping) requests
            var timer = new Stopwatch();
            timer.Start();

            promises = grains.Select(grain => grain.LongMethod(12));
            try
            {
                Task.WhenAll(promises).Wait();
            }
            catch (Exception ex)
            {
                if (ex.GetBaseException() is TimeoutException)
                {
                    Assert.True(false, "Long turns queued up and caused a timeout");
                }
                else
                {
                    throw;
                }
            }
            timer.Stop();

            Assert.True(timer.Elapsed.TotalSeconds < 40, "Long turns queued up and caused an extended runtime");
        }
    }
}