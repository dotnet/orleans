using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Essentially lightweight FixedThreadPool
    /// </summary>
    internal class ThreadPoolExecutor : IExecutor
    {
        private readonly QueueWorkItemCallback[] runningWorkItems;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue;

        private readonly ThreadPoolExecutorOptions options;

        private readonly ThreadPoolTrackingStatistic statistics;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            this.options = options;

            workQueue = new BlockingCollection<QueueWorkItemCallback>(options.PreserveOrder
                ? (IProducerConsumerCollection<QueueWorkItemCallback>) new ConcurrentQueue<QueueWorkItemCallback>()
                : new ConcurrentBag<QueueWorkItemCallback>());

            statistics = new ThreadPoolTrackingStatistic(options.Name);

            options.CancellationToken.Register(() =>
            {
                var chanceToGracefullyExit = QueueWorkItemCallback.NoOpQueueWorkItemCallback;
                workQueue.Add(chanceToGracefullyExit);
                workQueue.CompleteAdding();
            });

            runningWorkItems = new QueueWorkItemCallback[GetThreadSlotIndex(options.DegreeOfParallelism)];
            
            for (var threadIndex = 0; threadIndex < options.DegreeOfParallelism; threadIndex++)
            {
                RunWorker(threadIndex);
            }
        }
        
        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new QueueWorkItemCallback(
                callback,
                state,
                options.WorkItemExecutionTimeTreshold,
                options.WorkItemStatusProvider);

            TrackRequestEnqueue(workItem);

            workQueue.Add(workItem);
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            var healthy = true;
            foreach (var workItem in runningWorkItems)
            {
                if (workItem != null && workItem.IsFrozen())
                {
                    healthy = false;
                    options.Log.Error(
                        ErrorCode.SchedulerTurnTooLong,
                        string.Format(SR.WorkItem_LongExecutionTime, workItem.GetWorkItemStatus(true)));
                }
            }

            return healthy;
        }

        protected void ProcessQueue(ExecutorThreadContext threadContext)
        {
            TrackExecutionStart();

            try
            {
                RunNonBatching(threadContext);
            }
            finally
            {
                TrackExecutionStop();
            }
        }

        private void RunWorker(int threadIndex)
        {
            var threadContext = new ExecutorThreadContext(CreateWorkItemFilters(GetThreadSlotIndex(threadIndex)));
            new ThreadPerTaskExecutor(
                    new SingleThreadExecutorOptions(
                        options.Name + threadIndex,
                        options.StageType,
                        options.CancellationToken,
                        options.Log,
                        options.FaultHandler))
                .QueueWorkItem(_ => ProcessQueue(threadContext));
        }

        private int GetThreadSlotIndex(int threadIndex)
        {
            // false sharing prevention
            const int padding = 64;
            return threadIndex * padding;
        }

        protected void RunNonBatching(ExecutorThreadContext threadContext)
        {
            try
            {
                while (!workQueue.IsCompleted &&
                       (!options.CancellationToken.IsCancellationRequested || options.DrainAfterCancel))
                {
                    QueueWorkItemCallback workItem;
                    try
                    {
                        workItem = workQueue.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    if (!workItem.ExecuteWithFilters(threadContext.WorkItemFilters))
                    {
                        break;
                    }
                }
            }
            catch (Exception exc)
            {
                options.Log.Error(ErrorCode.SchedulerWorkerThreadExc, SR.Executor_Thread_Caugth_Exception, exc);
            }
        }

        #region StatisticsTracking

        private void TrackRequestEnqueue(QueueWorkItemCallback workItem)
        {
            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectQueueStats)
            {
                statistics.OnEnQueueRequest(1, WorkQueueCount, workItem);
            }
        }

        private void TrackExecutionStart()
        {
            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                statistics.OnStartExecution();
            }
        }

        private void TrackExecutionStop()
        {
            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                statistics.OnStopExecution();
            }
        }

        private void TrackRequestDequeue(QueueWorkItemCallback workItem)
        {
            var waitTime = workItem.TimeSinceQueued;
            if (waitTime > options.DelayWarningThreshold && !System.Diagnostics.Debugger.IsAttached)
            {
                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                options.Log.Warn(
                    ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime,
                    SR.Queue_Item_WaitTime, waitTime, workItem.State);
            }

            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectQueueStats)
            {
                statistics.OnDeQueueRequest(workItem);
            }
        }

        private void TrackProcessingStart()
        {
            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                statistics.OnStartProcessing();
            }
        }

        private void TrackProcessingStop()
        {
            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                statistics.OnStopProcessing();
                statistics.IncrementNumberOfProcessed();
            }
        }

        #endregion

        private WorkItemFilter[] CreateWorkItemFilters(int executorWorkItemSlotIndex)
        {
            return WorkItemFilter.CreateChain(new Func<WorkItemFilter>[]
            {
                () => new OuterExceptionHandlerFilter(options.Log),
                () => new StatisticsTrackingFilter(this),
                () => new RunningWorkItemsTracker(this, executorWorkItemSlotIndex),
                () => new ExceptionHandlerFilter(options.Log)
            });
        }

        private sealed class StatisticsTrackingFilter : WorkItemFilter
        {
            public StatisticsTrackingFilter(ThreadPoolExecutor executor) : base(
                onActionExecuting: workItem =>
                {
                    executor.TrackRequestDequeue(workItem);
                    executor.TrackProcessingStart();
                },
                onActionExecuted: workItem => { executor.TrackProcessingStop(); })
            {
            }
        }

        private sealed class RunningWorkItemsTracker : WorkItemFilter
        {
            public RunningWorkItemsTracker(ThreadPoolExecutor executor, int workItemSlotIndex) : base(
                onActionExecuting: workItem => { executor.runningWorkItems[workItemSlotIndex] = workItem; },
                onActionExecuted: workItem => { executor.runningWorkItems[workItemSlotIndex] = null; })
            {
            }
        }

        internal sealed class ExecutorThreadContext
        {
            public ExecutorThreadContext(WorkItemFilter[] workItemFilters)
            {
                WorkItemFilters = workItemFilters;
            }

            public WorkItemFilter[] WorkItemFilters { get; }
        }

        internal static class SR
        {
            public static string WorkItem_ExecutionTime = "WorkItem={0} Executing for {1} {2}";

            public static string WorkItem_LongExecutionTime = "Work item {0} has been executing for long time.";

            public static string Executor_Thread_Caugth_Exception = "Executor thread caugth exception:";

            public static string Queue_Item_WaitTime = "Queue wait time of {0} for Item {1}";
        }
    }

    internal delegate string WorkItemStatusProvider(object state, bool detailed);
    
    internal class QueueWorkItemCallback : ITimeInterval
    {
        public static QueueWorkItemCallback NoOpQueueWorkItemCallback =
            new QueueWorkItemCallback(s => { }, null, TimeSpan.MaxValue);

        private readonly WaitCallback callback;

        private readonly WorkItemStatusProvider statusProvider;

        private readonly object state;

        private readonly TimeSpan executionTimeTreshold;

        private readonly DateTime enqueueTime;

        private ITimeInterval timeInterval;

        // for lightweight execution time tracking 
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
            this.enqueueTime = DateTime.UtcNow;
        }

        public TimeSpan Elapsed => timeInterval.Elapsed;

        internal TimeSpan TimeSinceQueued => Utils.Since(enqueueTime);

        internal object State => state;

        public void Execute()
        {
            executionStart = DateTime.UtcNow;
            callback.Invoke(state);
        }

        public bool ExecuteWithFilters(IEnumerable<WorkItemFilter> actionFilters)
        {
            return actionFilters.First().ExecuteWorkItem(this);
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
            return string.Format(
                ThreadPoolExecutor.SR.WorkItem_ExecutionTime, state, Utils.Since(executionStart),
                statusProvider?.Invoke(state, detailed));
        }

        internal bool IsFrozen()
        {
            if (timeInterval != null)
            {
                return timeInterval.Elapsed > executionTimeTreshold;
            }

            return false;
        }
    }

    internal class ThreadPoolTrackingStatistic
    {
        private readonly ThreadTrackingStatistic threadTracking;

        private readonly QueueTrackingStatistic queueTracking;

        public ThreadPoolTrackingStatistic(string name)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(name);
            }

            if (ExecutorOptions.TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(name, null); // todo: null
            }
        }

        public void OnStartExecution()
        {
            queueTracking.OnStartExecution();
        }

        public void OnDeQueueRequest(QueueWorkItemCallback workItem)
        {
            queueTracking.OnDeQueueRequest(workItem);
        }

        public void OnEnQueueRequest(int i, int workQueueCount, QueueWorkItemCallback workItem)
        {
            queueTracking.OnDeQueueRequest(workItem);
        }

        public void OnStartProcessing()
        {
            threadTracking.OnStartProcessing();
        }

        internal void OnStopProcessing()
        {
            threadTracking.OnStopProcessing();
        }

        internal void IncrementNumberOfProcessed()
        {
            threadTracking.IncrementNumberOfProcessed();
        }

        public void OnStopExecution()
        {
            threadTracking.OnStopExecution();
        }
    }
}