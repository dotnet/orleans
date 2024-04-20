#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class GrainTimer : IGrainTimer
    {
        private readonly Func<object?, Task> asyncCallback;
        private readonly TimeSpan dueTime;
        private readonly TimeSpan timerFrequency;
        private readonly ILogger logger;
        private readonly object currentlyExecutingTickTaskLock = new();
        private readonly OrleansTaskScheduler scheduler;
        private readonly IActivationData? activationData;
        private DateTime previousTickTime;
        private int totalNumTicks;
        private volatile AsyncTaskSafeTimer? timer;
        private volatile Task? currentlyExecutingTickTask;

        public string Name { get; }

        private GrainTimer(OrleansTaskScheduler scheduler, IActivationData? activationData, ILogger logger, Func<object?, Task> asyncCallback, object? state, TimeSpan dueTime, TimeSpan period, string? name)
        {
            var ctxt = RuntimeContext.CurrentGrainContext;
            scheduler.CheckSchedulingContextValidity(ctxt);
            this.scheduler = scheduler;
            this.activationData = activationData;
            this.logger = logger;
            this.Name = name ?? string.Empty;
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
            Func<object?, Task> asyncCallback,
            object state,
            TimeSpan dueTime,
            TimeSpan period,
            string? name = null,
            IActivationData? activationData = null)
        {
            return new GrainTimer(scheduler, activationData, logger, asyncCallback, state, dueTime, period, name);
        }

        public void Start()
        {
            if (timer is not { } asyncTimer)
            {
                throw new ObjectDisposedException(String.Format("The timer {0} was already disposed.", GetFullName()));
            }

            asyncTimer.Start(dueTime, timerFrequency);
        }

        public void Stop()
        {
            // Stop the timer from ticking, but don't dispose it yet.
            if (timer is { } asyncTimer)
            {
                asyncTimer.Stop();
            }
        }

        private async Task TimerTick(object state, IGrainContext context)
        {
            if (timer is null)
            {
                // The timer has been disposed already.
                return;
            }
            
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
            if (timer is null)
            {
                return;
            }

            try
            {
                RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake.
                lock (this.currentlyExecutingTickTaskLock)
                {
                    if (timer is null)
                    {
                        return;
                    }

                    currentlyExecutingTickTask = InvokeAsyncCallback(state);
                }

                await currentlyExecutingTickTask;
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
                {
                    DisposeTimer();
                }
            }
        }

        private async Task InvokeAsyncCallback(object state)
        {
            // This is called under a lock, so ensure that the method yields before invoking a callback
            // which could take a different lock and potentially cause a deadlock.
            await Task.Yield();

            totalNumTicks++;

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace(ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {0}", GetFullName());
            }

            await asyncCallback(state);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace(ErrorCode.TimerAfterCallback, "Completed timer callback for timer {0}", GetFullName());
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
            if (timer is not { } asyncTimer)
            {
                return true;
            }

            // check underlying SafeTimer (checking that .NET thread pool does not starve this timer)
            if (!asyncTimer.CheckTimerFreeze(lastCheckTime, () => Name)) return false; 
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
            {
                DisposeTimer();
            }
        }

        private void DisposeTimer()
        {
            var asyncTimer = Interlocked.CompareExchange(ref timer, null, timer);
            if (asyncTimer == null)
            {
                return;
            }

            try
            {
                asyncTimer.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error disposing timer {TimerName}", GetFullName());
            }
            
            activationData?.OnTimerDisposed(this);
        }
    }
}
