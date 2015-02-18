using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using UnitTestGrains;

namespace UnitTests.TimerTests
{
    [TestClass]
    public class TimerOrleansTest : UnitTestBase
    {
        public TimerOrleansTest() : base(false){ }

        [TestCleanup]
        public void Cleanup()
        {
            ResetAllAdditionalRuntimes();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Timers")]
        public void TimerOrleansTest_Basic()
        {
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = TimerGrainFactory.GetGrain(GetRandomGrainId());
                TimeSpan period = grain.GetTimerPeriod().Result;
                Thread.Sleep(period.Multiply(10));
                int last = grain.GetCounter().Result;
                Console.WriteLine("value = " + last);
                //Assert.IsTrue(10 == last || 9 == last, last.ToString());

                grain.StopDefaultTimer().Wait();
                Thread.Sleep(period.Multiply(10));
                int curr = grain.GetCounter().Result;
                //Assert.IsTrue(curr == last || curr == last + 1, curr.ToString() + " " + last.ToString());
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Timers")]
        public void TimerOrleansTest_Parallel()
        {
            TimeSpan period = TimeSpan.Zero;
            List<ITimerGrain> grains = new List<ITimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = TimerGrainFactory.GetGrain(GetRandomGrainId());
                grains.Add(grain);
                period = grain.GetTimerPeriod().Result; // activate grains
            }

            Thread.Sleep(period.Multiply(10));
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                int last = grain.GetCounter().Result;
                Console.WriteLine("value = " + last);
                //Assert.AreEqual(10, last);
            }
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                grain.StopDefaultTimer().Wait();
            }
        }

#if DEBUG || REVISIT
        [TestMethod, TestCategory("Timers")]
        public void TimerOrleansTest_MultipleTimers()
        {
            ITimerGrain grain = TimerGrainFactory.GetGrain(GetRandomGrainId());
            Thread.Sleep(50);
            grain.StopDefaultTimer().Wait();
            int last = grain.GetCounter().Result;
            Assert.AreEqual(1, last);
            grain.SetCounter(0).Wait();

            TimeSpan period = grain.GetTimerPeriod().Result;
            grain.StartTimer("TIMER 1");
            Thread.Sleep(period.Multiply(5));
            last = grain.GetCounter().Result;
            Assert.AreEqual(5, last);

            grain.StartTimer("TIMER 2");
            Thread.Sleep(period.Multiply(5));
            last = grain.GetCounter().Result;
            Assert.AreEqual(15, last);

            grain.StopTimer("TIMER 1").Wait();
            Thread.Sleep(period.Multiply(5));
            last = grain.GetCounter().Result;
            Assert.AreEqual(20, last);

            grain.StopTimer("TIMER 2").Wait();
            last = grain.GetCounter().Result;
            Assert.AreEqual(20, last);
        }

        [TestMethod, TestCategory("Timers")]
        public void TimerOrleansTest_Migration()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            ITimerGrain grain = TimerGrainFactory.GetGrain(GetRandomGrainId());
            TimeSpan period = grain.GetTimerPeriod().Result;
            Thread.Sleep(period.Multiply(10));
            int last = grain.GetCounter().Result;
            Console.WriteLine("value = " + last);
            Assert.IsTrue(last >= 10 && last <= 11, "last = " + last.ToString(CultureInfo.InvariantCulture));

            StartAdditionalOrleans();

            //IManagementGrain mgmtGrain = Orleans.Silo.SystemManagement;
            //mgmtGrain.SuspendHost(Orleans.SiloAddress).Wait();

            Thread.Sleep(period.Multiply(10));
            grain.StopDefaultTimer().Wait();
            stopwatch.Stop();

            last = grain.GetCounter().Result;
            Assert.IsTrue(last >= 20);
            double maximalNumTicks = stopwatch.Elapsed.Divide(grain.GetTimerPeriod().Result);
            Assert.IsTrue(last <= maximalNumTicks);
            //mgmtGrain.ResumeHost(Orleans.SiloAddress).Wait();
            Console.WriteLine("Total Elaped time = " + (stopwatch.ElapsedMilliseconds / 1000.0) + " sec. Expected Ticks = " + maximalNumTicks + ". Actual ticks = " + last);
        }

        [TestMethod, TestCategory("Timers")]
        public void TimerOrleansTest_Stress()
        {
            ITimerGrain grain = TimerGrainFactory.GetGrain(GetRandomGrainId());
            TimeSpan period = grain.GetTimerPeriod().Result;
            Thread.Sleep(period.Multiply(10));
            int last = grain.GetCounter().Result;
            Console.WriteLine("last = " + last);
            Assert.IsTrue(last >= 10 && last <= 11, "last = " + last.ToString(CultureInfo.InvariantCulture));

            // block the grain so ticks can not be delivered now but will accumulate and be delivered later.
            grain.LongWait(period.Multiply(10)).Wait();
            Thread.Sleep(period.Multiply(5)); // let all delayed ticks to tick.
            
            grain.StopDefaultTimer().Wait();

            last = grain.GetCounter().Result;
            Console.WriteLine("\n\n\n\n last = " + last + "\n\n\n\n");
            Assert.IsTrue(last >= 23 && last <= 27, "last = " + last.ToString(CultureInfo.InvariantCulture));
        }
#endif

        [TestMethod, TestCategory("Nightly"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            ITimerCallGrain grain = null;

            Exception error = null;
            try
            {
                grain = TimerCallGrainFactory.GetGrain(0);

                await grain.StartTimer(testName, delay);

                await Task.Delay(wait);

                int tickCount = await grain.GetTickCount();
                Assert.AreEqual(1, tickCount, "Should be {0} timer callback", tickCount);

                Exception err = await grain.GetException();
                Assert.IsNull(err, "Should be no exceptions during timer callback");
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc);
                error = exc;
            }

            try
            {
                if (grain != null) await grain.StopTimer(testName);
            }
            catch (Exception exc)
            {
                // Ignore
                Console.WriteLine("Ignoring exception from StopTimer : {0}", exc);
            }

            if (error != null)
            {
                Assert.Fail("Test {0} failed with error {1}", testName, error);
            }
        }
    }

    public class TimerGrainReferenceProxy // : UnitTestGrains.ITimerGrain
    {
        private readonly ITimerGrain grain;
        private readonly ITimerPersistantGrain persistantGrain;
        private readonly bool persistant;

        public TimerGrainReferenceProxy(bool persist)
        {
            persistant = persist;
            if (persistant)
            {
                persistantGrain = TimerPersistantGrainFactory.GetGrain(UnitTestBase.GetRandomGrainId());
            }
            else
            {
                grain = TimerGrainFactory.GetGrain(UnitTestBase.GetRandomGrainId());
            }
        }

        public Task StopDefaultTimer()
        {
            if (persistant) return persistantGrain.StopDefaultTimer();
            else return grain.StopDefaultTimer();
        }
        public Task<TimeSpan> GetTimerPeriod()
        {
            if (persistant) return persistantGrain.GetTimerPeriod();
            else return grain.GetTimerPeriod();
        }
        public Task<int> GetCounter()
        {
            if (persistant) return persistantGrain.GetCounter();
            else return grain.GetCounter();
        }
        public Task SetCounter(int value)
        {
            if (persistant) return persistantGrain.SetCounter(value);
            else return grain.SetCounter(value);
        }
        public Task StartTimer(string timerName)
        {
            if (persistant) return persistantGrain.StartTimer(timerName);
            else return grain.StartTimer(timerName);
        }
        public Task StopTimer(string timerName)
        {
            if (persistant) return persistantGrain.StartTimer(timerName);
            else return grain.StartTimer(timerName);
        }
    }
}
