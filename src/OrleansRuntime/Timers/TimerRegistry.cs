﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace Orleans.Timers
{
    internal class TimerRegistry : ITimerRegistry
    {
        private readonly OrleansTaskScheduler scheduler;
        private readonly ILoggerFactory loggerFactory;
        public TimerRegistry(OrleansTaskScheduler scheduler, ILoggerFactory loggerFactory)
        {
            this.scheduler = scheduler;
            this.loggerFactory = loggerFactory;
        }

        public IDisposable RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = GrainTimer.FromTaskCallback(this.scheduler, this.loggerFactory, asyncCallback, state, dueTime, period, activationData: grain.Data);
            grain.Data.OnTimerCreated(timer);
            timer.Start();
            return timer;
        }
    }
}
