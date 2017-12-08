using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Clustering.ServiceFabric.Utilities
{
    internal static class WaitHandleExtensions
    {
        internal static Task<bool> WaitAsync(this WaitHandle handle, TimeSpan timeout)
        {
            // If the handle is already set, return synchronously.
            if (handle.WaitOne(0))
                return Task.FromResult(true);

            // If the timeout is zero, return synchronously.
            if (timeout == TimeSpan.Zero)
                return Task.FromResult(false);
            
            // Register a delegate on the thread pool to execute when the handle is set.
            var tcs = new TaskCompletionSource<bool>();
            ThreadPool.RegisterWaitForSingleObject(
                handle,
                (state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut),
                tcs,
                timeout,
                true);
            return tcs.Task;
        }
    }
}
