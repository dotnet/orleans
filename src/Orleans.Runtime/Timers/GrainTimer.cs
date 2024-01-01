using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal sealed class GrainTimer : IGrainTimer
    {
        private Func<object, Task> asyncCallback;
        private AsyncTaskSafeTimer timer;
        private readonly TimeSpan dueTime;
        private readonly TimeSpan timerFrequency;
        private DateTime previousTickTime;
        private int totalNumTicks;
        private readonly ILogger logger;
        private volatile Task currentlyExecutingTickTask;
        private readonly object currentlyExecutingTickTaskLock = new();
        private readonly IGrainContext grainContext;

        public string Name { get; }

        private bool TimerAlreadyStopped { get { return timer == null || asyncCallback == null; } }

        private GrainTimer(IGrainContext activationData, ILogger logger, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name)
        {
            var ctxt = RuntimeContext.Current;
            if (ctxt is null)
            {
                throw new InvalidSchedulingContextException(
                    "Current grain context is null. "
                     + "Please make sure you are not trying to create a Timer from outside Orleans Task Scheduler, "
                     + "which will be the case if you create it inside Task.Run.");
            }

            this.grainContext = activationData;
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
            ILogger logger,
            Func<object, Task> asyncCallback,
            object state,
            TimeSpan dueTime,
            TimeSpan period,
            string name = null,
            IGrainContext grainContext = null)
        {
            return new GrainTimer(grainContext, logger, asyncCallback, state, dueTime, period, name);
        }

        public void Start()
        {
            if (TimerAlreadyStopped)
                throw new ObjectDisposedException(string.Format("The timer {0} was already disposed.", GetFullName()));

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
                var workItem = new AsyncClosureWorkItem(() => ForwardToAsyncCallback(state), this.Name, context);
                context.Scheduler.QueueWorkItem(workItem);
                await workItem.Task;
            }
            catch (InvalidSchedulingContextException exc)
            {
                logger.LogError(
                    (int)ErrorCode.Timer_InvalidContext,
                    exc,
                    "Caught an InvalidSchedulingContextException on timer {TimerName}, context is {GrainContext}. Going to dispose this timer!",
                    GetFullName(),
                    context);
                DisposeTimer();
            }
        }

        private async Task ForwardToAsyncCallback(object state)
        {
            // AsyncSafeTimer ensures that calls to this method are serialized.
            if (TimerAlreadyStopped) return;

            try
            {
                RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake.
                lock (this.currentlyExecutingTickTaskLock)
                {
                    if (TimerAlreadyStopped) return;

                    totalNumTicks++;

                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace((int)ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {TimerName}", GetFullName());

                    currentlyExecutingTickTask = asyncCallback(state);
                }
                await currentlyExecutingTickTask;

                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)ErrorCode.TimerAfterCallback, "Completed timer callback for timer {TimerName}", GetFullName());
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.Timer_GrainTimerCallbackError,
                    exc,
                    "Caught and ignored exception thrown from timer callback for timer {TimerName}",
                    GetFullName());
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

        private string GetFullName() => $"GrainTimer.{Name} TimerCallbackHandler:{asyncCallback?.Target}->{asyncCallback?.Method}";

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
            DisposeTimer();
            asyncCallback = null;
        }

        private void DisposeTimer()
        {
            var tmp = timer;
            if (tmp == null) return;

            Utils.SafeExecute(tmp.Dispose);
            timer = null;
            lock (this.currentlyExecutingTickTaskLock)
            {
                asyncCallback = null;
            }

            grainContext?.GetComponent<IGrainTimerRegistry>().OnTimerDisposed(this);
        }
    }
}
