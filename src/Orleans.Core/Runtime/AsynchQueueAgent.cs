using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal abstract class AsynchQueueAgent<T> : AsynchAgent<ThreadPoolExecutor>
    {
        protected AsynchQueueAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
            : base(nameSuffix, executorService, loggerFactory)
        {
            ProcessAction = state => Process((T)state);
        }

        public WaitCallback ProcessAction { get; }

        public void QueueRequest(T request)
        {
            if (State != ThreadState.Running)
            {
                Log.LogWarning($"Invalid usage attempt of {Name} agent in {State.ToString()} state");
                return;
            }

            executor.QueueWorkItem(ProcessAction, request);
        }

        public int Count => executor.WorkQueueCount;

        protected abstract void Process(T request);

        protected override ExecutorOptions ExecutorOptions =>
             new ThreadPoolExecutorOptions(
                Name,
                GetType(),
                Cts.Token,
                Log,
                drainAfterCancel: DrainAfterCancel,
                faultHandler: ExecutorFaultHandler);
        

        internal static TimeSpan TurnWarningLengthThreshold { get; set; }

        //// This is the maximum number of pending work items for a single activation before we write a warning log.
        internal LimitValue MaxPendingItemsLimit { get; private set; }

        internal TimeSpan DelayWarningThreshold { get; private set; }

        protected virtual bool DrainAfterCancel { get; } = false;

        //  trackQueueStatistic, from OrleansTaskScheduler todo: add? 
    }
}
