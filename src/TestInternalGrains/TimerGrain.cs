using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;


namespace UnitTestGrains
{
    public class TimerGrain : Grain, ITimerGrain
    {
        int counter = 0;
        Dictionary<string, IDisposable> allTimers;
        IDisposable defaultTimer;
        private static readonly TimeSpan period = TimeSpan.FromMilliseconds(100);
        string DefaultTimerName = "DEFAULT TIMER";
        ISchedulingContext context;

        private TraceLogger logger;

        public override Task OnActivateAsync()
        {
            logger = (TraceLogger)this.GetLogger("TimerGrain_" + base.Data.Address.ToString());
            context = RuntimeContext.Current.ActivationContext;
            defaultTimer = this.RegisterTimer(Tick, DefaultTimerName, TimeSpan.Zero, period);
            allTimers = new Dictionary<string, IDisposable>();
            return TaskDone.Done;
        }
        public Task StopDefaultTimer()
        {
            defaultTimer.Dispose();
            return TaskDone.Done;
        }

        private Task Tick(object data)
        {
            counter++;
            logger.Info(data.ToString() + " Tick # " + counter + " RuntimeContext = " + RuntimeContext.Current.ActivationContext.ToString());

            // make sure we run in the right activation context.
            logger.Assert(ErrorCode.Runtime_Error_100146, context == RuntimeContext.Current.ActivationContext);

            string name = (string)data;
            IDisposable timer = null;
            if (name == DefaultTimerName)
            {
                timer = defaultTimer;
            }
            else
            {
                timer = allTimers[(string)data];
            }
            logger.Assert(ErrorCode.Runtime_Error_100146, timer != null);
            return TaskDone.Done;
        }

        public Task<TimeSpan> GetTimerPeriod()
        {
            return Task<TimeSpan>.FromResult(period);
        }

        public Task<int> GetCounter()
        {
            return Task<int>.FromResult(counter);
        }
        public Task SetCounter(int value)
        {
            lock (this)
            {
                counter = value;
            }
            return TaskDone.Done;
        }
        public Task StartTimer(string timerName)
        {
            IDisposable timer = this.RegisterTimer(Tick, timerName, TimeSpan.Zero, period);
            allTimers.Add(timerName, timer);
            return TaskDone.Done;
        }

        public Task StopTimer(string timerName)
        {
            IDisposable timer = allTimers[timerName];
            timer.Dispose();
            return TaskDone.Done;
        }

        public Task LongWait(TimeSpan time)
        {
            Thread.Sleep(time);
            return TaskDone.Done;
        }

    }

    public class TimerCallGrain : Grain, ITimerCallGrain
    {
        private int tickCount;
        private Exception tickException;
        private IDisposable timer;
        private string timerName;
        private ISchedulingContext context;
        private TaskScheduler activationTaskScheduler;

        private Logger logger;

        public Task<int> GetTickCount() { return Task.FromResult(tickCount); }
        public Task<Exception> GetException() { return Task.FromResult(tickException); }

        public override Task OnActivateAsync()
        {
            logger = this.GetLogger("TimerCallGrain_" + base.Data.Address);
            context = RuntimeContext.Current.ActivationContext;
            activationTaskScheduler = TaskScheduler.Current;
            return TaskDone.Done;
        }

        public Task StartTimer(string name, TimeSpan delay)
        {
            logger.Info("StartTimer Name={0} Delay={1}", name, delay);
            this.timerName = name;
            this.timer = base.RegisterTimer(TimerTick, name, delay, Constants.INFINITE_TIMESPAN); // One shot timer
            return TaskDone.Done;
        }

        public Task StopTimer(string name)
        {
            logger.Info("StopTimer Name={0}", name);
            if (name != this.timerName)
            {
                throw new ArgumentException(string.Format("Wrong timer name: Expected={0} Actual={1}", this.timerName, name));
            }
            timer.Dispose();
            return TaskDone.Done;
        }

        private async Task TimerTick(object data)
        {
            try
            {
                await ProcessTimerTick(data);
            }
            catch (Exception exc)
            {
                this.tickException = exc;
                throw;
            }
        }

        private async Task ProcessTimerTick(object data)
        {
            string step = "TimerTick";
            LogStatus(step);
            // make sure we run in the right activation context.
            CheckRuntimeContext(step);

            string name = (string)data;
            if (name != this.timerName)
            {
                throw new ArgumentException(string.Format("Wrong timer name: Expected={0} Actual={1}", this.timerName, name));
            }

            ISimpleGrain grain = GrainFactory.GetGrain<ISimpleGrain>(0, SimpleGrain.SimpleGrainNamePrefix);

            LogStatus("Before grain call #1");
            await grain.SetA(tickCount);
            step = "After grain call #1";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before Delay");
            await Task.Delay(TimeSpan.FromSeconds(1));
            step = "After Delay";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #2");
            await grain.SetB(tickCount);
            step = "After grain call #2";
            LogStatus(step);
            CheckRuntimeContext(step);

            LogStatus("Before grain call #3");
            int res = await grain.GetAxB();
            step = "After grain call #3 - Result = " + res;
            LogStatus(step);
            CheckRuntimeContext(step);

            tickCount++;
        }

        private void CheckRuntimeContext(string what)
        {
            if (RuntimeContext.Current.ActivationContext == null 
                || !RuntimeContext.Current.ActivationContext.Equals(context))
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected activation context: Expected={1} Actual={2}",
                                  what, context, RuntimeContext.Current.ActivationContext));
            }
            if (TaskScheduler.Current.Equals(activationTaskScheduler) && TaskScheduler.Current is ActivationTaskScheduler)
            {
                // Everything is as expected
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("{0} in timer callback with unexpected TaskScheduler.Current context: Expected={1} Actual={2}",
                                  what, activationTaskScheduler, TaskScheduler.Current));
            }
        }

        private void LogStatus(string what)
        {
            logger.Info("{0} Tick # {1} - {2} - RuntimeContext.Current={3} TaskScheduler.Current={4} CurrentWorkerThread={5}",
                        timerName, tickCount, what, RuntimeContext.Current, TaskScheduler.Current,
                        WorkerPoolThread.CurrentWorkerThread);
        }

    }
}
