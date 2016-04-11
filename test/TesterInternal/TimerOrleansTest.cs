using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;
using Xunit.Abstractions;

namespace UnitTests.TimerTests
{
    public class TimerOrleansTest : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;

        public TimerOrleansTest(ITestOutputHelper output, DefaultClusterFixture fixture)
            : base(fixture)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("Timers")]
        public void TimerOrleansTest_Basic()
        {
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = GrainClient.GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                TimeSpan period = grain.GetTimerPeriod().Result;
                Thread.Sleep(period.Multiply(10));
                int last = grain.GetCounter().Result;
                output.WriteLine("value = " + last);
                //Assert.IsTrue(10 == last || 9 == last, last.ToString());

                grain.StopDefaultTimer().Wait();
                Thread.Sleep(period.Multiply(10));
                int curr = grain.GetCounter().Result;
                //Assert.IsTrue(curr == last || curr == last + 1, curr.ToString() + " " + last.ToString());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Timers")]
        public void TimerOrleansTest_Parallel()
        {
            TimeSpan period = TimeSpan.Zero;
            List<ITimerGrain> grains = new List<ITimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = GrainClient.GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                grains.Add(grain);
                period = grain.GetTimerPeriod().Result; // activate grains
            }

            Thread.Sleep(period.Multiply(10));
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                int last = grain.GetCounter().Result;
                output.WriteLine("value = " + last);
                //Assert.AreEqual(10, last);
            }
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                grain.StopDefaultTimer().Wait();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Timers")]
        public void TimerOrleansTest_Migration()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            ITimerGrain grain = GrainClient.GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
            TimeSpan period = grain.GetTimerPeriod().Result;
            Thread.Sleep(period.Multiply(10));
            int last = grain.GetCounter().Result;
            output.WriteLine("value = " + last);
            Assert.IsTrue(last >= 10 && last <= 11, "last = " + last.ToString(CultureInfo.InvariantCulture));

            var additionalSilo = this.HostedCluster.StartAdditionalSilo();
            try
            {
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
                output.WriteLine("Total Elaped time = " + (stopwatch.ElapsedMilliseconds / 1000.0) + " sec. Expected Ticks = " + maximalNumTicks + ". Actual ticks = " + last);
            }
            finally
            {
                if (this.HostedCluster.SecondarySilos.Count > 1)
                {
                    // do not leave unnecessarily too many silos running
                    this.HostedCluster.StopSilo(additionalSilo);
                }
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            ITimerCallGrain grain = null;

            Exception error = null;
            try
            {
                grain = GrainClient.GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await Task.Delay(wait);

                int tickCount = await grain.GetTickCount();
                Assert.AreEqual(1, tickCount, "Should be {0} timer callback", tickCount);

                Exception err = await grain.GetException();
                Assert.IsNull(err, "Should be no exceptions during timer callback");
            }
            catch (Exception exc)
            {
                output.WriteLine(exc);
                error = exc;
            }

            try
            {
                if (grain != null) await grain.StopTimer(testName);
            }
            catch (Exception exc)
            {
                // Ignore
                output.WriteLine("Ignoring exception from StopTimer : {0}", exc);
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
                persistantGrain = GrainClient.GrainFactory.GetGrain<ITimerPersistantGrain>(TestUtils.GetRandomGrainId());
            }
            else
            {
                grain = GrainClient.GrainFactory.GetGrain<ITimerGrain>(TestUtils.GetRandomGrainId());
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
            if (persistant) return persistantGrain.StopTimer(timerName);
            else return grain.StopTimer(timerName);
        }
    }
}
