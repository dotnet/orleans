using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime
{
    internal class QueuedExecutor : IExecutor, IDisposable
    {
        private QueueTrackingStatistic queueTracking;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue = new BlockingCollection<QueueWorkItemCallback>();
        private readonly CancellationTokenSource cancellationTokenSource;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public QueuedExecutor(string name, CancellationTokenSource cts)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(name);
            }

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif

            cancellationTokenSource = cts;


            new ThreadPerTaskExecutor(name).QueueWorkItem(_ => ProcessQueue());
        }

        public int WorkQueueLength => workQueue.Count;

        public void QueueWorkItem(WaitCallback callBack, object state = null)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, requestQueue.Count, request);
            }
#endif

            workQueue.Add(new QueueWorkItemCallback(callBack, state));
        }
        
        public void Dispose()
        {
            workQueue.Dispose();
        }

        protected void ProcessQueue()
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                queueTracking.OnStartExecution();
            }
#endif
            try
            {
                RunNonBatching();
            }
            finally
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    queueTracking.OnStopExecution();
                }
#endif
            }
        }


        protected void RunNonBatching()
        {
            while (true)
            {
                if (cancellationTokenSource.IsCancellationRequested)
                {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking.OnStopExecution();
            }
#endif
                    return;
                }

                var workItem = workQueue.Take();
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(request);
                }
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartProcessing();
                }
#endif
                workItem.ExecuteWorkItem();
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopProcessing();
                    threadTracking.IncrementNumberOfProcessed();
                }
#endif
            }
        }

        internal sealed class QueueWorkItemCallback
        {
            private readonly WaitCallback callback;

            private readonly object state;

            public QueueWorkItemCallback(WaitCallback callback, object state)
            {
                this.callback = callback;
                this.state = state;
            }

            public void ExecuteWorkItem()
            {
                callback.Invoke(state);
            }
        }
    }
}