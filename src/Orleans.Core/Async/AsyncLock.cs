using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// An async mutual exclusion mechanism that supports scoping via ‘using’.
    /// </summary>
    /// <remarks>
    /// (Adapted from http://blogs.msdn.com/b/pfxteam/archive/2012/02/12/10266988.aspx)
    ///
    /// When programming with <b>async</b>, the <b>lock</b> keyword is problematic:
    /// <list type="bullet">
    ///     <item><b>lock</b> will cause the thread to block while it waits for exclusive access to the critical section of code.</item>
    ///     <item>The <b>await</b> keyword cannot be used within the scope of a <b>lock</b> construct.</item>
    /// </list>
    ///
    /// It is still useful, at times, to provide exclusive access to a critical section of code. AsyncLock provides semantics
    /// that correspond to that of a (non-recursive) mutex, while maintaining compatibility with the tenets of async programming.
    /// </remarks>
    /// <example>
    /// The following example implements some work that needs to be done under lock:
    /// <code>
    /// class Test
    /// {
    ///     private AsyncLock _initLock = new AsyncLock();
    ///     public async Task&lt;int> WorkUnderLock()
    ///     {
    ///             using (await _initLock.LockAsync()) // analogous to lock(_initLock)
    ///             {
    ///                  return await DoSomeWork();
    ///             }
    ///     }
    /// }
    /// </code>
    /// </example>
    ///
    /// We decided to keep the implementation simple and mimic the semantics of a regular mutex as much as possible.
    /// 1) AsyncLock is NOT IDisposable, since we don't want to give the developer an option to erroneously manually dispose the lock
    /// while there may be some unreleased LockReleasers.
    /// 2) AsyncLock does NOT have to implement the Finalizer function. The underlying resource of SemaphoreSlim will be eventually released by the .NET,
    /// when SemaphoreSlim is finalized. Having finalizer for AsyncLock will not speed it up.
    /// 3) LockReleaser is IDisposable to implement the "using" pattern.
    /// 4) LockReleaser does NOT have to implement the Finalizer function. If users forget to Dispose the LockReleaser (analogous to forgetting to release a mutex)
    /// the AsyncLock will remain locked, which may potentially cause deadlock. This is OK, since these are the exact regular mutex semantics - if one forgets to unlock the mutex, it stays locked.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable")]
    internal class AsyncLock
    {
        private readonly SemaphoreSlim semaphore;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLock"/> class.
        /// </summary>
        public AsyncLock()
        {
            semaphore = new SemaphoreSlim(1);
        }

        /// <summary>
        /// Acquires the lock asynchronously.
        /// </summary>
        /// <returns>An <see cref="IDisposable"/> which must be used to release the lock.</returns>
        public ValueTask<IDisposable> LockAsync()
        {
            Task wait = semaphore.WaitAsync();
            if (wait.IsCompletedSuccessfully)
            {
                return new(new LockReleaser(this));
            }
            else
            {
                return LockAsyncInternal(this, wait);

                static async ValueTask<IDisposable> LockAsyncInternal(AsyncLock self, Task waitTask)
                {
                    await waitTask.ConfigureAwait(false);
                    return new LockReleaser(self);
                }
            }
        }

        private class LockReleaser : IDisposable
        {
            private AsyncLock target;

            internal LockReleaser(AsyncLock target)
            {
                this.target = target;
            }

            public void Dispose()
            {
                if (target == null)
                    return;

                // first null it, next Release, so even if Release throws, we don't hold the reference any more.
                AsyncLock tmp = target;
                target = null;
                try
                {
                    tmp.semaphore.Release();
                }
                catch (Exception) { } // just ignore the Exception
            }
        }
    }
}