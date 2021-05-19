using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class GrainTimer : IGrainTimer
    {
        private Func<object, Task> asyncCallback;
        private AsyncTaskSafeTimer timer;
        private readonly TimeSpan dueTime;
        private readonly TimeSpan timerFrequency;
        private DateTime previousTickTime;
        private int totalNumTicks;
        private readonly ILogger logger;
        private Task currentlyExecutingTickTask;
        private readonly OrleansTaskScheduler scheduler;
        private readonly IActivationData activationData;

        public string Name { get; }
        
        private bool TimerAlreadyStopped { get { return timer == null || asyncCallback == null; } }

        private GrainTimer(OrleansTaskScheduler scheduler, IActivationData activationData, ILogger logger, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name)
        {
            var ctxt = RuntimeContext.CurrentGrainContext;
            scheduler.CheckSchedulingContextValidity(ctxt);
            this.scheduler = scheduler;
            this.activationData = activationData;
            this.logger = logger;
            this.Name = name;
            this.asyncCallback = asyncCallback;
            timer = new AsyncTaskSafeTimer(logger, 
                stateObj => TimerTick(stateObj, ctxt),
                state);
            this.dueTime = dueTime;
            timerFrequency = period;
            previousTickTime = DateTime.UtcNow;
            totalNumTicks = 0;
        }

        internal static IGrainTimer FromTaskCallback(
            OrleansTaskScheduler scheduler,
            ILogger logger,
            Func<object, Task> asyncCallback,
            object state,
            TimeSpan dueTime,
            TimeSpan period,
            string name = null,
            IActivationData activationData = null)
        {
            return new GrainTimer(scheduler, activationData, logger, asyncCallback, state, dueTime, period, name);
        }

        public void Start()
        {
            if (TimerAlreadyStopped)
                throw new ObjectDisposedException(String.Format("The timer {0} was already disposed.", GetFullName()));

            timer.Start(dueTime, timerFrequency);
        }

        public void Stop()
        {
            asyncCallback = null;
        }

        private async Task TimerTick(object state, IGrainContext context)
        {
            if (TimerAlreadyStopped)
                return;
            try
            {
                // Schedule call back to grain context
                await this.scheduler.QueueNamedTask(() => ForwardToAsyncCallback(state), context, this.Name);
            }
            catch (InvalidSchedulingContextException exc)
            {
                logger.Error(ErrorCode.Timer_InvalidContext,
                    string.Format("Caught an InvalidSchedulingContextException on timer {0}, context is {1}. Going to dispose this timer!",
                        GetFullName(), context), exc);
                DisposeTimer();
            }
        }

        private async Task ForwardToAsyncCallback(object state)
        {
            // AsyncSafeTimer ensures that calls to this method are serialized.
            var callback = asyncCallback;
            if (TimerAlreadyStopped) return;
            
            totalNumTicks++;

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace(ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {0}", GetFullName());

            try
            {
                RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake. 
                currentlyExecutingTickTask = callback(state);
                await currentlyExecutingTickTask;
                
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.TimerAfterCallback, "Completed timer callback for timer {0}", GetFullName());
            }
            catch (Exception exc)
            {
                logger.Error( 
                    ErrorCode.Timer_GrainTimerCallbackError,
                    string.Format( "Caught and ignored exception: {0} with message: {1} thrown from timer callback {2}",
                        exc.GetType(),
                        exc.Message,
                        GetFullName()),
                    exc);       
            }
            finally
            {
                previousTickTime = DateTime.UtcNow;
                currentlyExecutingTickTask = null;
                // if this is not a repeating timer, then we can
                // dispose of the timer.
                if (timerFrequency == Constants.INFINITE_TIMESPAN)
                    DisposeTimer();                
            }
        }

        public Task GetCurrentlyExecutingTickTask()
        {
            return currentlyExecutingTickTask ?? Task.CompletedTask;
        }

        private string GetFullName()
        {
            var callback = asyncCallback;
            var callbackTarget = callback?.Target?.ToString() ?? string.Empty; 
            var callbackMethodInfo = callback?.GetMethodInfo()?.ToString() ?? string.Empty;
            return $"GrainTimer.{this.Name ?? string.Empty} TimerCallbackHandler:{callbackTarget ?? string.Empty}->{callbackMethodInfo ?? string.Empty}";
        }

        // The reason we need to check CheckTimerFreeze on both the SafeTimer and this GrainTimer
        // is that SafeTimer may tick OK (no starvation by .NET thread pool), but then scheduler.QueueWorkItem
        // may not execute and starve this GrainTimer callback.
        public bool CheckTimerFreeze(DateTime lastCheckTime)
        {
            if (TimerAlreadyStopped) return true;
            // check underlying SafeTimer (checking that .NET thread pool does not starve this timer)
            if (!timer.CheckTimerFreeze(lastCheckTime, () => Name)) return false; 
            // if SafeTimer failed the check, no need to check GrainTimer too, since it will fail as well.
            
            // check myself (checking that scheduler.QueueWorkItem does not starve this timer)
            return SafeTimerBase.CheckTimerDelay(previousTickTime, totalNumTicks,
                dueTime, timerFrequency, logger, GetFullName, ErrorCode.Timer_TimerInsideGrainIsNotTicking, true);
        }

        public bool CheckTimerDelay()
        {
            return SafeTimerBase.CheckTimerDelay(previousTickTime, totalNumTicks,
                dueTime, timerFrequency, logger, GetFullName, ErrorCode.Timer_TimerInsideGrainIsNotTicking, false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Maybe called by finalizer thread with disposing=false. As per guidelines, in such a case do not touch other objects.
        // Dispose() may be called multiple times
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                DisposeTimer();
            
            asyncCallback = null;
        }

        private void DisposeTimer()
        {
            var tmp = timer;
            if (tmp == null) return;

            Utils.SafeExecute(tmp.Dispose);
            timer = null;
            asyncCallback = null;
            activationData?.OnTimerDisposed(this);
        }
    }
}
