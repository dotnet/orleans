using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ThreadPoolExecutor : IExecutor
    {
        private QueueTrackingStatistic queueTracking;

        // private readonly HashSet<WorkerPoolThread> pool;
        //  private int runningThreadCount;

        // internal readonly int activeThreads = 3; // todo: accept as parameter 
        //   internal readonly TimeSpan MaxWorkQueueWait;
        //     internal readonly bool EnableWorkerThreadInjection;
        //     private readonly ICorePerformanceMetrics performanceMetrics;
        //     private readonly ILoggerFactory loggerFactory;
        //   internal bool ShouldInjectWorkerThread { get { return EnableWorkerThreadInjection && runningThreadCount < WorkerPoolThread.MAX_THREAD_COUNT_TO_REPLACE; } }
        //  private readonly ILogger timerLogger;

        private SafeTimer longTurnTimer;
        private readonly QueueWorkItemCallback[] QueueWorkItemRefs;
        private readonly BlockingCollection<QueueWorkItemCallback> workQueue = new BlockingCollection<QueueWorkItemCallback>();
        private readonly CancellationToken cancellationToken;
        private readonly bool drainAfterCancel;

#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(options.StageName);
            }

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
            this.drainAfterCancel = options.DrainAfterCancel;
            cancellationToken = options.CancellationToken;
            cancellationToken.Register(() =>
            {
                // allow threads to get a chance to exit gracefully.
                workQueue.Add(QueueWorkItemCallback.NoOpQueueWorkItemCallback);
                workQueue.CompleteAdding();
            });

            // padding reduces false sharing
            var padding = 100;
            QueueWorkItemRefs = new QueueWorkItemCallback[options.DegreeOfParallelism * padding];
            for (var createThreadCount = 0; createThreadCount < options.DegreeOfParallelism; createThreadCount++)
            {
                var executorWorkItemSlotIndex = createThreadCount * padding;
                new ThreadPerTaskExecutor(new SingleThreadExecutorOptions(options.StageName + createThreadCount))
                    .QueueWorkItem(_ => ProcessQueue(executorWorkItemSlotIndex));
            }
        }

        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            var workItem = new QueueWorkItemCallback(callback, state);
            TrackRequestEnqueue(workItem);
            workQueue.Add(workItem);
        }

        protected void ProcessQueue(int workItemSlotIndex)
        {
            TrackExecutionStart();
            try
            {
                RunNonBatching(workItemSlotIndex);
            }
            finally
            {
                TrackExecutionStop();
            }
        }

        protected void RunNonBatching(int workItemSlotIndex) // slotNumber
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

                QueueWorkItemRefs[workItemSlotIndex] = workItem;
                TrackRequestDequeue(workItem);
                TrackProcessingStart();

                workItem.ExecuteWorkItem();

                TrackProcessingStop();
                QueueWorkItemRefs[workItemSlotIndex] = null;
            }
        }

        #region StatisticsTracking

        private void TrackRequestEnqueue(QueueWorkItemCallback workItem)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, WorkQueueCount, workItem);
            }
#endif
        }

        private void TrackExecutionStart()
        {

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                queueTracking.OnStartExecution();
            }
#endif
        }

        private void TrackExecutionStop()
        {

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    queueTracking.OnStopExecution();
                }
#endif
        }

        //internal bool DoHealthCheck()
        //{
        //    if (!IsFrozen()) return true;

        //    Log.Error(ErrorCode.SchedulerTurnTooLong, string.Format(
        //        "Worker pool thread {0} (ManagedThreadId={1}) has been busy for long time: {2}",
        //        Name, ManagedThreadId, GetThreadStatus(true)));
        //    return false;
        //}

        private void TrackRequestDequeue(QueueWorkItemCallback workItem)
        {
            //// Capture the queue wait time for this task
            //TimeSpan waitTime = todo.TimeSinceQueued;
            //if (waitTime > scheduler.DelayWarningThreshold && !Debugger.IsAttached)
            //{
            //    SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
            //    Log.Warn(ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime, "Queue wait time of {0} for Item {1}", waitTime, todo);
            //}
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(workItem);
                }
#endif
        }

        private void TrackProcessingStart()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartProcessing();
                }
#endif
        }

        private void TrackProcessingStop()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopProcessing();
                    threadTracking.IncrementNumberOfProcessed();
                }
#endif
        }

        #endregion

        internal class QueueWorkItemCallback : ITimeInterval
        {
            public static QueueWorkItemCallback NoOpQueueWorkItemCallback = new QueueWorkItemCallback(s => { }, null);

            private readonly WaitCallback callback;

            private readonly Func<object, string> statusProvider;

            private readonly object state;

            private ITimeInterval timeInterval;

            public QueueWorkItemCallback(WaitCallback callback, object state)
            {
                this.callback = callback;
                this.state = state;
            }

            public QueueWorkItemCallback(WaitCallback callback, object state, Func<object, string> statusProvider)
                : this(callback, state)
            {
                this.statusProvider = statusProvider;
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

            private string GetWorkItemStatus(bool detailed)
            {
                return statusProvider?.Invoke(state);
            }

            private bool IsFrozen()
            {
                if (timeInterval != null)
                {
                    // return timeInterval.Elapsed > OrleansTaskScheduler.TurnWarningLengthThreshold;
                }
                return false;
                //  // If there is no active Task, check current wokr item, if any.
                //   bool frozenWorkItem = CurrentWorkItem != null && Utils.Since(currentWorkItemStarted) > OrleansTaskScheduler.TurnWarningLengthThreshold;
                //   return frozenWorkItem;
            }
            public TimeSpan Elapsed => timeInterval.Elapsed;
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            throw new NotImplementedException();
        }
    }
}