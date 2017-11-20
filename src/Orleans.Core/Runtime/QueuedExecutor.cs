using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime
{
    internal class QueuedExecutor : IExecutor
    {
        private QueueTrackingStatistic queueTracking;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue = new BlockingCollection<QueueWorkItemCallback>();
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly bool drainAfterCancel;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public QueuedExecutor(string name, CancellationTokenSource cts, bool drainAfterCancel)
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
            this.drainAfterCancel = drainAfterCancel;
            cancellationTokenSource = cts;
            cancellationTokenSource.Token.Register(() =>
            {
                // allow threads to get a chance to exit gracefully.
                workQueue.Add(QueueWorkItemCallback.NoOpQueueWorkItemCallback);
                workQueue.CompleteAdding();
            });
            new ThreadPerTaskExecutor(name).QueueWorkItem(_ => ProcessQueue());
        }

        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            var workItemCallback = new QueueWorkItemCallback(callback, state);


#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, WorkQueueCount, workItemCallback);
            }
#endif

            workQueue.Add(workItemCallback);
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
                if (!drainAfterCancel && cancellationTokenSource.IsCancellationRequested ||
                    workQueue.IsCompleted)
                {
                    return;
                }
                
                QueueWorkItemCallback workItem;
                try
                {
                    workItem = workQueue.Take();
                }
                catch (InvalidOperationException)
                {
                    break;
                }

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(workItem);
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

        internal  class QueueWorkItemCallback : ITimeInterval
        {
            public static QueueWorkItemCallback NoOpQueueWorkItemCallback = new QueueWorkItemCallback(s => {}, null);

            private readonly WaitCallback callback;

            private readonly object state;

            private ITimeInterval timeInterval;

            public QueueWorkItemCallback(WaitCallback callback, object state)
            {
                this.callback = callback;
                this.state = state;
            }

            public void ExecuteWorkItem()
            {
                callback.Invoke(state);
            }
            
            public void Start()
            {
                timeInterval = TimeIntervalFactory.CreateTimeInterval(true);
                timeInterval.Start();
            }

            public void Stop()
            {
                timeInterval.Stop();
            }

            public void Restart()
            {
                timeInterval.Restart();
            }

            public TimeSpan Elapsed => timeInterval.Elapsed;
        }
    }
}