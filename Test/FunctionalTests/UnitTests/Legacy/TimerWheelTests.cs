using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.General
{
    [TestClass]
    public class TimerWheelTests
    {
       private class TestCallback : ITimebound
        {
            private readonly TimeSpan timeout;
            private readonly DateTime started;
            private readonly ResultHandle result;

            public TestCallback(TimeSpan time, ResultHandle res)
            {
                timeout = time;
                started = DateTime.UtcNow;
                result = res;
            }

            public void OnTimeout()
            {
                Console.WriteLine("TestCallback:OnTimeout requested timeout of " + timeout + " ms and ticked after " + (DateTime.UtcNow - started).TotalMilliseconds);
                if (result !=null)
                result.Done = true;
            }
            public TimeSpan RequestedTimeout()
            {
                return timeout;
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Timers")]
        public void TimerWheel_Basic()
        {
            // one bucket for every second.
            TimeSpan period = TimeSpan.FromMilliseconds(1000);
            CoarseGrainTimerWheel coarseGrainTimerWheel = new CoarseGrainTimerWheel(10, period.Multiply(10));
            ResultHandle result1 = new ResultHandle();
            TestCallback call = new TestCallback(TimeSpan.FromMilliseconds(2500), result1);
            coarseGrainTimerWheel.ScheduleForTimeout(call, call.RequestedTimeout());

            ResultHandle result2 = new ResultHandle();
            call = new TestCallback(TimeSpan.FromMilliseconds(3500), result2);
            coarseGrainTimerWheel.ScheduleForTimeout(call, call.RequestedTimeout());

            ResultHandle result3 = new ResultHandle();
            call = new TestCallback(TimeSpan.FromMilliseconds(4500), result3);
            coarseGrainTimerWheel.ScheduleForTimeout(call, call.RequestedTimeout());

            Assert.IsTrue(result1.WaitForFinished(call.RequestedTimeout() + period.Multiply(2)));
            Assert.IsTrue(result2.WaitForFinished(call.RequestedTimeout() + period.Multiply(2)));
            Assert.IsTrue(result3.WaitForFinished(call.RequestedTimeout() + period.Multiply(2)));
        }
    }
}
