using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ThreadPoolExecutor : IExecutor, IHealthCheckable
    {
        private QueueTrackingStatistic queueTracking;

        // private readonly HashSet<WorkerPoolThread> pool;
        //  private int runningThreadCount;
        
        //   internal readonly TimeSpan MaxWorkQueueWait;
        //     internal readonly bool EnableWorkerThreadInjection;
        //     private readonly ICorePerformanceMetrics performanceMetrics;
        //     private readonly ILoggerFactory loggerFactory;
        //   internal bool ShouldInjectWorkerThread { get { return EnableWorkerThreadInjection && runningThreadCount < WorkerPoolThread.MAX_THREAD_COUNT_TO_REPLACE; } }
        //  private readonly ILogger timerLogger;

        private readonly QueueWorkItemCallback[] QueueWorkItemRefs;
        private readonly BlockingCollection<QueueWorkItemCallback> workQueue = new BlockingCollection<QueueWorkItemCallback>();
        private readonly ThreadPoolExecutorOptions _executorOptions;

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
            _executorOptions = options;
            _executorOptions.CancellationToken.Register(() =>
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
            var workItem = new QueueWorkItemCallback(
                callback, 
                state,
                _executorOptions.WorkItemExecutionTimeTreshold,
                _executorOptions.WorkItemStatusProvider);

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

        protected void RunNonBatching(int workItemSlotIndex)
        {
            while (true)
            {
                if (!_executorOptions.DrainAfterCancel && _executorOptions.CancellationToken.IsCancellationRequested ||
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
            public static QueueWorkItemCallback NoOpQueueWorkItemCallback = new QueueWorkItemCallback(s => { }, null, TimeSpan.MaxValue);

            private readonly WaitCallback callback;

            private readonly WorkItemStatusProvider statusProvider;

            private readonly object state;

            private readonly TimeSpan executionTimeTreshold;

            private ITimeInterval timeInterval;

            // lightweight mean of execution time tracking 
            private DateTime executionStart;

            public QueueWorkItemCallback(
                WaitCallback callback, 
                object state, 
                TimeSpan executionTimeTreshold,
                WorkItemStatusProvider statusProvider = null)
            {
                this.callback = callback;
                this.state = state;
                this.executionTimeTreshold = executionTimeTreshold;
                this.statusProvider = statusProvider;
            }

            public void ExecuteWorkItem()
            {
                executionStart = DateTime.UtcNow;
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

            internal string GetWorkItemStatus(bool detailed)
            {
                return $"WorkItem={state} Executing for {Utils.Since(executionStart)} {statusProvider?.Invoke(state, detailed)}";
            }

            internal bool CheckHealth()
            {
                return !IsFrozen();
            }

            private bool IsFrozen()
            {
                if (timeInterval != null)
                {
                     return timeInterval.Elapsed > executionTimeTreshold;
                }

                return false;
                //  // If there is no active Task, check current work item, if any.
                //   bool frozenWorkItem = CurrentWorkItem != null && Utils.Since(currentWorkItemStarted) > OrleansTaskScheduler.TurnWarningLengthThreshold;
                //   return frozenWorkItem;
            }

            public TimeSpan Elapsed => timeInterval.Elapsed;
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            var ok = true;
            foreach (var workItem in QueueWorkItemRefs)
            {
                if (workItem != null && !workItem.CheckHealth())
                {
                    ok = false;
                    _executorOptions.Log.Error(ErrorCode.SchedulerTurnTooLong,
                        $"Work item {0} has been executing for long time:GetThreadStatus(true) {workItem.GetWorkItemStatus(true)}");
                }
            }

            return ok;
        }
    }

    internal delegate string WorkItemStatusProvider(object state, bool detailed);
}