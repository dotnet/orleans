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
            executor.QueueWorkItem(ProcessAction, request);
        }

        protected abstract void Process(T request);

        public override void Stop()
        {
            //   requestQueue?.CompleteAdding(); - ensured by CTS usage
            base.Stop();
        }

        public int Count => executor.WorkQueueLength;

        #region IDisposable Members

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking.OnStopExecution();
            }
#endif
            base.Dispose(disposing);
        }

        #endregion
    }
}
