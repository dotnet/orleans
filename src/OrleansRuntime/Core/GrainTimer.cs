/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Threading;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal class GrainTimer : IDisposable
    {
        private Func<object, Task> asyncCallback;
        private AsyncTaskSafeTimer timer;
        private readonly TimeSpan dueTime;
        private readonly TimeSpan timerFrequency;
        private DateTime previousTickTime;
        private int totalNumTicks;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("GrainTimer", TraceLogger.LoggerType.Runtime);
        private Task currentlyExecutingTickTask;
        private readonly ActivationData activationData;

        internal string Name { get; private set; }
        
        private bool TimerAlreadyStopped { get { return timer == null || asyncCallback == null; } }

        private GrainTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period, string name)
        {
            var ctxt = RuntimeContext.Current.ActivationContext;
            activationData = (ActivationData) RuntimeClient.Current.CurrentActivationData;

            this.Name = name;
            this.asyncCallback = asyncCallback;
            timer = new AsyncTaskSafeTimer( 
                stateObj => TimerAlreadyStopped ? TaskDone.Done : 
                    RuntimeClient.Current.ExecAsync(() => ForwardToAsyncCallback(stateObj), ctxt),
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

        internal static GrainTimer FromTaskCallback(
                Func<object, Task> asyncCallback,
                object state,
                TimeSpan dueTime,
                TimeSpan period,
                string name = null)
        {
            return new GrainTimer(asyncCallback, state, dueTime, period, name);
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

        private async Task ForwardToAsyncCallback(object state)
        {
            // AsyncSafeTimer ensures that calls to this method are serialized.
            if (TimerAlreadyStopped) return;
            
            totalNumTicks++;

            if (logger.IsVerbose3)
                logger.Verbose3(ErrorCode.TimerBeforeCallback, "About to make timer callback for timer {0}", GetFullName());

            try
            { 
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

        internal Task GetCurrentlyExecutingTickTask()
        {
            return currentlyExecutingTickTask ?? TaskDone.Done;
        }

        private string GetFullName()
        {
            return String.Format("GrainTimer.{0} TimerCallbackHandler:{1}->{2}",
               Name == null ? "" : Name + ".",
               (asyncCallback != null && asyncCallback.Target != null) ? asyncCallback.Target.ToString() : "",
               (asyncCallback != null && asyncCallback.Method != null) ? asyncCallback.Method.ToString() : "");
        }

        internal int GetNumTicks()
        {
            return totalNumTicks;
        }

        // The reason we need to check CheckTimerFreeze on both the SafeTimer and this GrainTimer
        // is that SafeTimer may tick OK (no starvation by .NET thread pool), but then scheduler.QueueWorkItem
        // may not execute and starve this GrainTimer callback.
        internal bool CheckTimerFreeze(DateTime lastCheckTime)
        {
            if (TimerAlreadyStopped) return true;
            // check underlying SafeTimer (checking that .NET thread pool does not starve this timer)
            if (!timer.CheckTimerFreeze(lastCheckTime, () => Name)) return false; 
            // if SafeTimer failed the check, no need to check GrainTimer too, since it will fail as well.
            
            // check myself (checking that scheduler.QueueWorkItem does not starve this timer)
            return SafeTimerBase.CheckTimerDelay(previousTickTime, totalNumTicks,
                dueTime, timerFrequency, logger, GetFullName, ErrorCode.Timer_TimerInsideGrainIsNotTicking, true);
        }

        internal bool CheckTimerDelay()
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
            if (activationData != null)
                activationData.OnTimerDisposed(this);
        }

        #endregion
    }
}
