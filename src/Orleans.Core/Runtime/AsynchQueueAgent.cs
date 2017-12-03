using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;

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

        protected abstract void Process(T request);
        
        public int Count => executor.WorkQueueCount;
    }
}
