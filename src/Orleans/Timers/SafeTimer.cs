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
