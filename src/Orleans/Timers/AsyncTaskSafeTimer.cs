using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class AsyncTaskSafeTimer : IDisposable
    {
        private readonly SafeTimerBase safeTimerBase;

        public AsyncTaskSafeTimer(Func<object, Task> asynTaskCallback, object state)
        {
            safeTimerBase = new SafeTimerBase(asynTaskCallback, state);
        }

        public AsyncTaskSafeTimer(Func<object, Task> asynTaskCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            safeTimerBase = new SafeTimerBase(asynTaskCallback, state, dueTime, period);
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

        // Maybe called by finalizer thread with disposing=false. As per guidelines, in such a case do not touch other objects.
        // Dispose() may be called multiple times
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "safeTimerBase")]
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                safeTimerBase.DisposeTimer();
            }
        }

        #endregion

        public bool CheckTimerFreeze(DateTime lastCheckTime, Func<string> callerName)
        {
            return safeTimerBase.CheckTimerFreeze(lastCheckTime, callerName);
        }
    }
}
