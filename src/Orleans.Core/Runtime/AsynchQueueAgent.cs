using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class AsynchQueueAgent<T> : AsynchAgent where T : IOutgoingMessage
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
                throw new InvalidOperationException("Not running agent usage attempt");
            }

            executor.QueueWorkItem(ProcessAction, request);
        }

        public int Count => executor.WorkQueueCount;

        protected abstract void Process(T request);

        protected override ExecutorOptions GetExecutorOptions()
        {
            return new ThreadPoolExecutorOptions(Name, GetType(), Cts.Token, Log, drainAfterCancel: DrainAfterCancel, faultHandler: ExecutorFaultHandler);
        }

        internal virtual bool DrainAfterCancel { get; } = false;
    }
}
