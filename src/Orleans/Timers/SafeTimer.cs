using System;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// SafeTimer - A wrapper class around .NET Timer objects, with some additional built-in safeguards against edge-case errors.
    /// 
    /// SafeTimer is a replacement for .NET Timer objects, and removes some of the more infrequently used method overloads for simplification.
    /// SafeTimer provides centralization of various "guard code" previously added in various places for handling edge-case fault conditions.
    /// 
    /// Log levels used: Recovered faults => Warning, Per-Timer operations => Verbose, Per-tick operations => Verbose3
    /// </summary>
    internal class SafeTimer : IDisposable
    {
        private readonly SafeTimerBase safeTimerBase;
        private readonly TimerCallback callbackFunc;

        public SafeTimer(TimerCallback callback, object state)
        {
            callbackFunc = callback;
            safeTimerBase = new SafeTimerBase(callbackFunc, state);
        }

        public SafeTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            callbackFunc = callback;
            safeTimerBase = new SafeTimerBase(callbackFunc, state, dueTime, period);
        }

        public void Start(TimeSpan dueTime, TimeSpan period)
        {
            safeTimerBase.Start(dueTime, period);
        }

        #region IDisposable Members

        public void Dispose()
        {
            safeTimerBase.Dispose();
        }

        // May be called by finalizer thread with disposing=false. As per guidelines, in such a case do not touch other objects.
        // Dispose() may be called multiple times
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                safeTimerBase.DisposeTimer();
            }
        }

        #endregion

        internal string GetFullName()
        {
            return String.Format("SafeTimer: {0}. ", callbackFunc != null ? callbackFunc.GetType().FullName : "");
        }

        public bool CheckTimerFreeze(DateTime lastCheckTime, Func<string> callerName)
        {
            return safeTimerBase.CheckTimerFreeze(lastCheckTime, callerName);
        }
    }
}
