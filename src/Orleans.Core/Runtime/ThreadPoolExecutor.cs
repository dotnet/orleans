using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ThreadPoolExecutor : IExecutor
    {
        private QueueTrackingStatistic queueTracking;

       // private readonly HashSet<WorkerPoolThread> pool;
      //  private int runningThreadCount;

        internal readonly int activeThreads = 3; // todo: accept as parameter 
        internal readonly TimeSpan MaxWorkQueueWait;
        internal readonly bool EnableWorkerThreadInjection;
   //     private readonly ICorePerformanceMetrics performanceMetrics;
   //     private readonly ILoggerFactory loggerFactory;
     //   internal bool ShouldInjectWorkerThread { get { return EnableWorkerThreadInjection && runningThreadCount < WorkerPoolThread.MAX_THREAD_COUNT_TO_REPLACE; } }
      //  private readonly ILogger timerLogger;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue = new BlockingCollection<QueueWorkItemCallback>();
        private readonly CancellationToken cancellationToken;
        private readonly bool drainAfterCancel;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadPoolExecutor(string name, CancellationToken ct, bool drainAfterCancel)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(name);
            }

            // move to initialize  stats method
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
            this.drainAfterCancel = drainAfterCancel;

            cancellationToken.Register(() =>
            {
                // allow threads to get a chance to exit gracefully.
                workQueue.Add(QueueWorkItemCallback.NoOpQueueWorkItemCallback);
                workQueue.CompleteAdding();
            });

            for (var createThreadCount = 0; createThreadCount < activeThreads; createThreadCount++)
            {
                new ThreadPerTaskExecutor(name + createThreadCount).QueueWorkItem(_ => ProcessQueue());
            }
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
                if (!drainAfterCancel && cancellationToken.IsCancellationRequested ||
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