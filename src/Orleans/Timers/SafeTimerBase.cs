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
    /// <summary>
    /// SafeTimerBase - an internal base class for implementing sync and async timers in Orleans.
    /// 
    /// </summary>
    internal class SafeTimerBase : IDisposable
    {
        private Timer               timer;
        private Func<object, Task>  asynTaskCallback;
        private TimerCallback       syncCallbackFunc;
        private TimeSpan            dueTime;
        private TimeSpan            timerFrequency;
        private bool                timerStarted;
        private DateTime            previousTickTime;
        private int                 totalNumTicks;
        private TraceLogger         logger;

        internal SafeTimerBase(Func<object, Task> asynTaskCallback, object state)
        {
            Init(asynTaskCallback, null, state, Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
        }

        internal SafeTimerBase(Func<object, Task> asynTaskCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            Init(asynTaskCallback, null, state, dueTime, period);
            Start(dueTime, period);
        }

        internal SafeTimerBase(TimerCallback syncCallback, object state)
        {
            Init(null, syncCallback, state, Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
        }

        internal SafeTimerBase(TimerCallback syncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            Init(null, syncCallback, state, dueTime, period);
            Start(dueTime, period);
        }

        public void Start(TimeSpan due, TimeSpan period)
        {
            if (timerStarted) throw new InvalidOperationException(String.Format("Calling start on timer {0} is not allowed, since it was already created in a started mode with specified due.", GetFullName()));
            if (period == TimeSpan.Zero) throw new ArgumentOutOfRangeException("period", period, "Cannot use TimeSpan.Zero for timer period");
           
            timerFrequency = period;
            dueTime = due;
            timerStarted = true;
            previousTickTime = DateTime.UtcNow;
            timer.Change(due, Constants.INFINITE_TIMESPAN);
        }

        private void Init(Func<object, Task> asynCallback, TimerCallback synCallback, object state, TimeSpan due, TimeSpan period)
        {
            if (synCallback == null && asynCallback == null) throw new ArgumentNullException("synCallback", "Cannot use null for both sync and asyncTask timer callbacks.");
            int numNonNulls = (asynCallback != null ? 1 : 0) + (synCallback != null ? 1 : 0);
            if (numNonNulls > 1) throw new ArgumentNullException("synCallback", "Cannot define more than one timer callbacks. Pick one.");
            if (period == TimeSpan.Zero) throw new ArgumentOutOfRangeException("period", period, "Cannot use TimeSpan.Zero for timer period");

            this.asynTaskCallback = asynCallback;
            syncCallbackFunc = synCallback;
            timerFrequency = period;
            this.dueTime = due;
            totalNumTicks = 0;

            logger = TraceLogger.GetLogger(GetFullName(), TraceLogger.LoggerType.Runtime);

            if (logger.IsVerbose) logger.Verbose(ErrorCode.TimerChanging, "Creating timer {0} with dueTime={1} period={2}", GetFullName(), due, period);

            timer = new Timer(HandleTimerCallback, state, Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
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
            {
                DisposeTimer();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal void DisposeTimer()
        {
            if (timer != null)
            {
                try
                {
                    var t = timer;
                    timer = null;
                    if (logger.IsVerbose) logger.Verbose(ErrorCode.TimerDisposing, "Disposing timer {0}", GetFullName());
                    t.Dispose();

                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.TimerDisposeError,
                        string.Format("Ignored error disposing timer {0}", GetFullName()), exc);
                }
            }
        }

        #endregion

        private string GetFullName()
        {
            // the type information is really useless and just too long. 
            string name;
            if (syncCallbackFunc != null)
                name = "sync";
            else if (asynTaskCallback != null)
                name = "asynTask";
            else
                throw new InvalidOperationException("invalid SafeTimerBase state");

            return String.Format("{0}.SafeTimerBase", name);
        }

        public bool CheckTimerFreeze(DateTime lastCheckTime, Func<string> callerName)
        {
            return CheckTimerDelay(previousTickTime, totalNumTicks,
                        dueTime, timerFrequency, logger, () => String.Format("{0}.{1}", GetFullName(), callerName()), ErrorCode.Timer_SafeTimerIsNotTicking, true);
        }

        public static bool CheckTimerDelay(DateTime previousTickTime, int totalNumTicks, 
                        TimeSpan dueTime, TimeSpan timerFrequency, TraceLogger logger, Func<string> getName, ErrorCode errorCode, bool freezeCheck)
        {
            TimeSpan timeSinceLastTick = DateTime.UtcNow - previousTickTime;
            TimeSpan exceptedTimeToNexTick = totalNumTicks == 0 ? dueTime : timerFrequency;
            TimeSpan exceptedTimeWithSlack;
            if (exceptedTimeToNexTick >= TimeSpan.FromSeconds(6))
            {
                exceptedTimeWithSlack = exceptedTimeToNexTick + TimeSpan.FromSeconds(3);
            }
            else
            {
                exceptedTimeWithSlack = exceptedTimeToNexTick.Multiply(1.5);
            }
            if (timeSinceLastTick <= exceptedTimeWithSlack) return true;

            // did not tick in the last period.
            var errMsg = String.Format("{0}{1} did not fire on time. Last fired at {2}, {3} since previous fire, should have fired after {4}.",
                freezeCheck ? "Watchdog Freeze Alert: " : "-", // 0
                getName == null ? "" : getName(),   // 1
                TraceLogger.PrintDate(previousTickTime), // 2
                timeSinceLastTick,                  // 3
                exceptedTimeToNexTick);             // 4

            if(freezeCheck)
            {
                logger.Error(errorCode, errMsg);
            }else
            {
                logger.Warn(errorCode, errMsg);
            }
            return false;
        }

        /// <summary>
        /// Changes the start time and the interval between method invocations for a timer, using TimeSpan values to measure time intervals.
        /// </summary>
        /// <param name="newDueTime">A TimeSpan representing the amount of time to delay before invoking the callback method specified when the Timer was constructed. Specify negative one (-1) milliseconds to prevent the timer from restarting. Specify zero (0) to restart the timer immediately.</param>
        /// <param name="period">The time interval between invocations of the callback method specified when the Timer was constructed. Specify negative one (-1) milliseconds to disable periodic signaling.</param>
        /// <returns><c>true</c> if the timer was successfully updated; otherwise, <c>false</c>.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            if (period == TimeSpan.Zero) throw new ArgumentOutOfRangeException("period", period, string.Format("Cannot use TimeSpan.Zero for timer {0} period", GetFullName()));

            if (timer == null) return false;

            timerFrequency = period;

            if (logger.IsVerbose) logger.Verbose(ErrorCode.TimerChanging, "Changing timer {0} to dueTime={1} period={2}", GetFullName(), newDueTime, period);

            try
            {
                // Queue first new timer tick
                return timer.Change(newDueTime, Constants.INFINITE_TIMESPAN);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.TimerChangeError,
                    string.Format("Error changing timer period - timer {0} not changed", GetFullName()), exc);
                return false;
            }
        }

        private void HandleTimerCallback(object state)
        {
            if (timer == null) return;

            if (asynTaskCallback != null)
            {
                HandleAsyncTaskTimerCallback(state);
            }
            else
            {
                HandleSyncTimerCallback(state);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void HandleSyncTimerCallback(object state)
        {
            try
            {
                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerBeforeCallback, "About to make sync timer callback for timer {0}", GetFullName());
                syncCallbackFunc(state);
                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerAfterCallback, "Completed sync timer callback for timer {0}", GetFullName());
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.TimerCallbackError, string.Format("Ignored exception {0} during sync timer callback {1}", exc.Message, GetFullName()), exc);
            }
            finally
            {
                previousTickTime = DateTime.UtcNow;
                // Queue next timer callback
                QueueNextTimerTick();
            }
        }

        private async void HandleAsyncTaskTimerCallback(object state)
        {
            if (timer == null) return;

            // There is a subtle race/issue here w.r.t unobserved promises.
            // It may happen than the asyncCallbackFunc will resolve some promises on which the higher level application code is depends upon
            // and this promise's await or CW will fire before the below code (after await or Finally) even runs.
            // In the unit test case this may lead to the situation where unit test has finished, but p1 or p2 or p3 have not been observed yet.
            // To properly fix this we may use a mutex/monitor to delay execution of asyncCallbackFunc until all CWs and Finally in the code below were scheduled 
            // (not until CW lambda was run, but just until CW function itself executed). 
            // This however will relay on scheduler executing these in separate threads to prevent deadlock, so needs to be done carefully. 
            // In particular, need to make sure we execute asyncCallbackFunc in another thread (so use StartNew instead of ExecuteWithSafeTryCatch).

            try
            {
                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerBeforeCallback, "About to make async task timer callback for timer {0}", GetFullName());
                await asynTaskCallback(state);
                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerAfterCallback, "Completed async task timer callback for timer {0}", GetFullName());
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.TimerCallbackError, string.Format("Ignored exception {0} during async task timer callback {1}", exc.Message, GetFullName()), exc);
            }
            finally
            {
                previousTickTime = DateTime.UtcNow;
                // Queue next timer callback
                QueueNextTimerTick();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void QueueNextTimerTick()
        {
            try
            {
                if (timer == null) return;

                totalNumTicks++;

                if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerChanging, "About to QueueNextTimerTick for timer {0}", GetFullName());

                if (timerFrequency == Constants.INFINITE_TIMESPAN)
                {
                    //timer.Change(Constants.INFINITE_TIMESPAN, Constants.INFINITE_TIMESPAN);
                    DisposeTimer();

                    if (logger.IsVerbose) logger.Verbose(ErrorCode.TimerStopped, "Timer {0} is now stopped and disposed", GetFullName());
                }
                else
                {
                    timer.Change(timerFrequency, Constants.INFINITE_TIMESPAN);

                    if (logger.IsVerbose3) logger.Verbose3(ErrorCode.TimerNextTick, "Queued next tick for timer {0} in {1}", GetFullName(), timerFrequency);
                }
            }
            catch (ObjectDisposedException ode)
            {
                logger.Warn(ErrorCode.TimerDisposeError,
                    string.Format("Timer {0} already disposed - will not queue next timer tick", GetFullName()), ode);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.TimerQueueTickError,
                    string.Format("Error queueing next timer tick - WARNING: timer {0} is now stopped", GetFullName()), exc);
            }
        }
    }
}
