using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace Orleans.Timers
{
    internal class TimerRegistry : ITimerRegistry
    {
        private readonly ILogger timerLogger;
        public TimerRegistry(ILoggerFactory loggerFactory)
        {
            this.timerLogger = loggerFactory.CreateLogger<GrainTimer>();
        }

        public IDisposable RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = GrainTimer.FromTaskCallback(this.timerLogger, asyncCallback, state, dueTime, period, activationData: grain?.Data);
            grain?.Data.OnTimerCreated(timer);
            timer.Start();
            return timer;
        }
    }
}
