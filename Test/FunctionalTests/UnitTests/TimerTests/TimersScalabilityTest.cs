using System;
using System.Threading;
using System.Linq;

using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests
{
    public class TimersScalabilityTest
    {
        private static int numTimers = 1000 * 1000;
        private static int[] ticks = new int[numTimers];
        private static object[] locks = new object[numTimers];
        private static Timer[] timers = new Timer[numTimers];
        private static AsyncTaskSafeTimer[] asyncTaskSafeTimers = new AsyncTaskSafeTimer[numTimers];
        private static TimeSpan dueTime = TimeSpan.FromMilliseconds(0);
        private static TimeSpan timerPeriod = TimeSpan.FromMilliseconds(1000);
        private static TimeSpan totalRunTime = TimeSpan.FromMilliseconds(60 * 1000);

        public TimersScalabilityTest() { }

        public void TimersScalabilityTest_1()
        {
            int workerThreads;
            int completionPortThreads;
            ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads);
            Console.WriteLine("workerThreads = " + workerThreads + " completionPortThreads = " + completionPortThreads);
            //ThreadPool.SetMinThreads(1000, 1000);

            for (int i = 0; i < numTimers; i++)
            {
                locks[i] = new object();
                //timers[i] = new Timer(TimerCallback, i, dueTime, timerPeriod);
                //asyncSafeTimers[i] = new AsyncSafeTimer(AsyncTimerCallback, i, dueTime, timerPeriod);
                asyncTaskSafeTimers[i] = new AsyncTaskSafeTimer(AsyncTaskTimerCallback, i, dueTime, timerPeriod);
            }
            Thread.Sleep(totalRunTime);

            TimeSpan runTime = totalRunTime - dueTime;
            int totalNumTicks = ticks.Sum();
            float actualTicksPerSecond = (float)totalNumTicks / (float)runTime.TotalSeconds;
            float expectedTicksPerSecond = (float)numTimers * ((float)1000.0 / (float)(timerPeriod.TotalMilliseconds));
            
            Console.WriteLine("totalNumTicks = " + totalNumTicks + " for " + numTimers + " timer with timer Period of " + timerPeriod.TotalMilliseconds + " ms for " + runTime.TotalSeconds + " sec total run time.");
            Console.WriteLine("Min timer = " + ticks.Min());
            Console.WriteLine("Max timer = " + ticks.Max());
            Console.WriteLine("Extected per timer = " + (float)runTime.TotalMilliseconds / (float)(timerPeriod.TotalMilliseconds));
            Console.WriteLine("ActualTicksPerSecond = " + actualTicksPerSecond);
            Console.WriteLine("ExpectedTicksPerSecond = " + expectedTicksPerSecond);
        }

        private void TimerCallback(object state)
        {
            int timerNumber = (int)state;
            lock (locks[timerNumber])
            {
                ticks[timerNumber]++;
                //int tickNumber = ticks[timerNumber];
            }
            //Console.WriteLine(timerNumber.ToString() + "->" + tickNumber);
        }

        private Task AsyncTimerCallback(object state)
        {
            TimerCallback(state);
            return TaskDone.Done;
        }

        private Task AsyncTaskTimerCallback(object state)
        {
            TimerCallback(state);
            return Task.FromResult(1);
        }
    }
}

