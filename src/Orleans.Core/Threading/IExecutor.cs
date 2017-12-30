using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Threading
{
    internal interface IExecutor : IHealthCheckable // todo: move IHealthCheckable to ThreadPoolExecutor
    {
        /// <summary>
        /// Executes the given command at some time in the future.  The command
        /// may execute in a new thread, in a pooled thread, or in the calling thread
        /// </summary>
        void QueueWorkItem(WaitCallback callback, object state = null);
    }
}