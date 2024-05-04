#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal sealed class GrainTimer : IGrainTimer
    {
        private readonly Func<object?, Task> asyncCallback;
        private readonly TimeSpan dueTime;
        private readonly TimeSpan timerFrequency;
        private readonly ILogger logger;
        private readonly IGrainContext grainContext;
        private readonly string name;
        private DateTime previousTickTime;
        private int totalNumTicks;
        private volatile AsyncTaskSafeTimer? timer;
        private readonly object? state;
        private volatile Task? currentlyExecutingTickTask;

        private GrainTimer(IGrainContext grainContext, ILogger logger, Func<object?, Task> asyncCallback, object? state, TimeSpan dueTime, TimeSpan period, string? name)
        {
            if (RuntimeContext.Current is null)
            {
                throw new InvalidSchedulingContextException(
                    "Current grain context is null. "
                     + "Please make sure you are not trying to create a Timer from outside Orleans Task Scheduler, "
                     + "which will be the case if you create it inside Task.Run.");
            }

            this.grainContext = grainContext;
            this.logger = logger;
            this.name = name ?? string.Empty;
            this.asyncCallback = asyncCallback;
            timer = new AsyncTaskSafeTimer(logger,
                static stateObj => ((GrainTimer)stateObj).TimerTick(),
                this);
            this.state = state;
            this.dueTime = dueTime;
            timerFrequency = period;
            previousTickTime = DateTime.UtcNow;
            totalNumTicks = 0;
        }

        internal static IGrainTimer FromTaskCallback(
            ILogger logger,
            Func<object?, Task> asyncCallback,
            object state,
            TimeSpan dueTime,
            TimeSpan period,
            string name,
            IGrainContext grainContext)
        {
            return new GrainTimer(grainContext, logger, asyncCallback, state, dueTime, period, name);
        }

        public void Start()
        {
            if (timer is not { } asyncTimer)
            {
                throw new ObjectDisposedException(GetDiagnosticName(), "The timer was already disposed.");
            }

            asyncTimer.Start(dueTime, timerFrequency);
        }

        public void Stop()
        {
            // Stop the timer from ticking, but don't dispose it yet since it might be mid-tick.
            timer?.Stop();
        }

        private async Task TimerTick()
        {
            // Schedule call back to grain context
            // AsyncSafeTimer ensures that calls to this method are serialized.
            var workItem = new AsyncClosureWorkItem(ForwardToAsyncCallback, this.name, grainContext);
            grainContext.Scheduler.QueueWorkItem(workItem);
            await workItem.Task;
        }

        private async Task ForwardToAsyncCallback()
        {
            // AsyncSafeTimer ensures that calls to this method are serialized.
            try
            {
                var tickTask = currentlyExecutingTickTask = InvokeTimerCallback();
                await tickTask;
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.Timer_GrainTimerCallbackError,
                    exc,
                    "Caught and ignored exception thrown from timer callback for timer {TimerName}",
                    GetDiagnosticName());
            }
            finally
            {
                previousTickTime = DateTime.UtcNow;
                currentlyExecutingTickTask = null;

                // If this is not a repeating timer, then we can dispose of the timer.
                if (timerFrequency == Constants.INFINITE_TIMESPAN)
                {
                    DisposeTimer();
                }
            }
        }

        private async Task InvokeTimerCallback()
        {
            // This is called under a lock, so ensure that the method yields before invoking a callback
            // which could take a different lock and potentially cause a deadlock.
            await Task.Yield();

            // If the timer was stopped or disposed since this was scheduled, terminate without invoking the callback.
            if (timer is not { IsStarted: true })
            {
                return;
            }

            // Clear any previous RequestContext, so it does not leak into this call by mistake.
            RequestContext.Clear();
            totalNumTicks++;

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace((int)ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {TimerName}", GetDiagnosticName());
            }

            await asyncCallback(state);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace((int)ErrorCode.TimerAfterCallback, "Completed timer callback for timer {TimerName}", GetDiagnosticName());
            }
        }

        public Task GetCurrentlyExecutingTickTask() => currentlyExecutingTickTask ?? Task.CompletedTask;

        private string GetDiagnosticName() => name switch
        {
            { Length: > 0 } => $"GrainTimer.{name} TimerCallbackHandler:{asyncCallback?.Target}->{asyncCallback?.Method}",
            _ => $"GrainTimer TimerCallbackHandler:{asyncCallback?.Target}->{asyncCallback?.Method}"
        };

        public void Dispose()
        {
            DisposeTimer();
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
                logger.LogError(ex, "Error disposing timer {TimerName}", GetDiagnosticName());
            }
            
            grainContext.GetComponent<IGrainTimerRegistry>()?.OnTimerDisposed(this);
        }
    }
}
