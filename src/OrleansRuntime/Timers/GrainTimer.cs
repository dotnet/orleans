using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;


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
        private static readonly Logger logger = LogManager.GetLogger("GrainTimer", LoggerType.Runtime);
        private Task currentlyExecutingTickTask;
        private readonly IActivationData activationData;

        public string Name { get; }
        
        private bool TimerAlreadyStopped { get { return timer == null || asyncCallback == null; } }

        private GrainTimer(IActivationData activationData, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name)
        {
            var ctxt = RuntimeContext.CurrentActivationContext;
            InsideRuntimeClient.Current.Scheduler.CheckSchedulingContextValidity(ctxt);
            this.activationData = activationData;

            this.Name = name;
            this.asyncCallback = asyncCallback;
            timer = new AsyncTaskSafeTimer( 
                stateObj => TimerTick(stateObj, ctxt),
                state);
            this.dueTime = dueTime;
            timerFrequency = period;
            previousTickTime = DateTime.UtcNow;
            totalNumTicks = 0;
        }

        internal static GrainTimer FromTimerCallback(
            TimerCallback callback,
            object state,
            TimeSpan dueTime,
            TimeSpan period,
            string name = null)
        {
            return new GrainTimer(
                null,
                ob =>
                {
                    if (callback != null)
                        callback(ob);
                    return TaskDone.Done;
                },
                state,
                dueTime,
                period,
                name);
        }

        internal static IGrainTimer FromTaskCallback(
                Func<object, Task> asyncCallback,
                object state,
                TimeSpan dueTime,
                TimeSpan period,
                string name = null,
                IActivationData activationData = null)
        {
            return new GrainTimer(activationData, asyncCallback, state, dueTime, period, name);
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

        private async Task TimerTick(object state, ISchedulingContext context)
        {
            if (TimerAlreadyStopped)
                return;
            try
            {
                await RuntimeClient.Current.ExecAsync(() => ForwardToAsyncCallback(state), context, Name);
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
            if (TimerAlreadyStopped) return;
            
            totalNumTicks++;

            if (logger.IsVerbose3)
                logger.Verbose3(ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {0}", GetFullName());

            try
            {
                RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake. 
                currentlyExecutingTickTask = asyncCallback(state);
                await currentlyExecutingTickTask;
                
                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerAfterCallback, "Completed timer callback for timer {0}", GetFullName());
            }
            catch (Exception exc)
            {
                logger.Error( 
                    ErrorCode.Timer_GrainTimerCallbackError,
                    string.Format( "Caught and ignored exception: {0} with mesagge: {1} thrown from timer callback {2}",
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
            return currentlyExecutingTickTask ?? TaskDone.Done;
        }

        private string GetFullName()
        {
            var callbackTarget = string.Empty;
            var callbackMethodInfo = string.Empty;
            if (asyncCallback != null)
            {
                if (asyncCallback.Target != null)
                {
                    callbackTarget = asyncCallback.Target.ToString();
                }

                var methodInfo = asyncCallback.GetMethodInfo();
                if (methodInfo != null)
                {
                    callbackMethodInfo = methodInfo.ToString();
                }
            }

            return string.Format("GrainTimer.{0} TimerCallbackHandler:{1}->{2}",
                Name == null ? "" : Name + ".", callbackTarget, callbackMethodInfo);
        }

        public int GetNumTicks()
        {
            return totalNumTicks;
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

        #region IDisposable Members

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

        #endregion
    }
}
