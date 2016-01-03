using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.SchedulerTests
{
    [TestClass]
    public class STSchedulerLongTurnTest : UnitTestSiloHost
    {
        public STSchedulerLongTurnTest()
            : base(true)
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
           // ResetDefaultRuntimes();
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_LongTurnTest()
        {
            // With two silos, there should be 16 threads.
            // We'll create way more grains than that to make sure we swamp the thread pools
            var grains = new List<IErrorGrain>();
            var grainFullName = typeof(ErrorGrain).FullName;
            for (int i = 0; i < 100; i++)
            {
                grains.Add(GrainClient.GrainFactory.GetGrain<IErrorGrain>(i, grainFullName));
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
                    Assert.Fail("Long turns queued up and caused a timeout");
                }
                else
                {
                    throw;
                }
            }
            timer.Stop();

            Assert.IsTrue(timer.Elapsed.TotalSeconds < 40, "Long turns queued up and caused an extended runtime");
        }
    }
}