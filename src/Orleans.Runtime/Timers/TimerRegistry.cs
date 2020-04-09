using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace Orleans.Timers
{
    internal class TimerRegistry : ITimerRegistry
    {
        private readonly OrleansTaskScheduler scheduler;
        private readonly ILogger timerLogger;
        public TimerRegistry(OrleansTaskScheduler scheduler, ILoggerFactory loggerFactory)
        {
            this.scheduler = scheduler;
            this.timerLogger = loggerFactory.CreateLogger<GrainTimer>();
        }

        public IDisposable RegisterTimer(IGrain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            if (grain is GrainReference) throw new ArgumentException("Passing a GrainReference as an argument. This method requires a grain implementation", nameof(grain));
            var timer = GrainTimer.FromTaskCallback(this.scheduler, this.timerLogger, asyncCallback, state, dueTime, period, activationData: grain?.GetActivationData());
            grain?.GetActivationData().OnTimerCreated(timer);
            timer.Start();
            return timer;
        }
    }
}
