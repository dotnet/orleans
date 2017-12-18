using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Essentially FixedThreadPool
    /// </summary>
    internal class ThreadPoolExecutor : IExecutor
    {
        private readonly WorkItemWrapper[] runningItems;

        private readonly BlockingCollection<WorkItemWrapper> workQueue;

        private readonly ThreadPoolExecutorOptions options;

        private readonly ThreadPoolTrackingStatistic statistic;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));

            workQueue = new BlockingCollection<WorkItemWrapper>(options.PreserveOrder
                ? (IProducerConsumerCollection<WorkItemWrapper>) new ConcurrentQueue<WorkItemWrapper>()
                : new ConcurrentBag<WorkItemWrapper>());

            statistic = new ThreadPoolTrackingStatistic(options.Name);

            runningItems = new WorkItemWrapper[GetThreadSlotIndex(options.DegreeOfParallelism)];

            options.CancellationToken.Register(() =>
            {
                var chanceToGracefullyExit = WorkItemWrapper.NoOpWorkItemWrapper;
                workQueue.Add(chanceToGracefullyExit);
                workQueue.CompleteAdding();
            });

            for (var threadIndex = 0; threadIndex < options.DegreeOfParallelism; threadIndex++)
            {
                RunWorker(threadIndex);
            }
        }
        
        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new WorkItemWrapper(
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
            foreach (var workItem in runningItems)
            {
                if (workItem != null && workItem.IsFrozen())
                {
                    healthy = false;
                    options.Log.Error(
                        ErrorCode.ExecutorTurnTooLong,
                        string.Format(SR.WorkItem_LongExecutionTime, workItem.GetWorkItemStatus(true)));
                }
            }

            return healthy;
        }

        private void ProcessQueue(ExecutorThreadContext threadContext)
        {
            statistic.OnStartExecution();

            try
            {
                while (!workQueue.IsCompleted &&
                       (!options.CancellationToken.IsCancellationRequested || options.DrainAfterCancel))
                {
                    WorkItemWrapper workItem;
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
            catch (Exception ex)
            {
                options.Log.Error(ErrorCode.ExecutorWorkerThreadExc, SR.Executor_Thread_Caugth_Exception, ex);
            }
            finally
            {
                statistic.OnStopExecution();
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

        private static int GetThreadSlotIndex(int threadIndex)
        {
            // false sharing prevention
            const int padding = 64;
            return threadIndex * padding;
        }
        
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

        #region StatisticsTracking

        private void TrackRequestEnqueue(WorkItemWrapper workItem)
        {
            statistic.OnEnQueueRequest(1, WorkQueueCount, workItem);
        }

        private void TrackRequestDequeue(WorkItemWrapper workItem)
        {
            var waitTime = workItem.TimeSinceQueued;
            if (waitTime > options.DelayWarningThreshold && !System.Diagnostics.Debugger.IsAttached)
            {
                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                options.Log.Warn(
                    ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime,
                    SR.Queue_Item_WaitTime, waitTime, workItem.State);
            }

            statistic.OnDeQueueRequest(workItem);
        }

        #endregion

        private sealed class StatisticsTrackingFilter : WorkItemFilter
        {
            public StatisticsTrackingFilter(ThreadPoolExecutor executor) : base(
                onActionExecuting: workItem =>
                {
                    executor.TrackRequestDequeue(workItem);
                    executor.statistic.OnStartProcessing();
                },
                onActionExecuted: workItem => 
                {
                    executor.statistic.OnStopProcessing();
                    executor.statistic.IncrementNumberOfProcessed();
                })
            {
            }
        }

        private sealed class RunningWorkItemsTracker : WorkItemFilter
        {
            public RunningWorkItemsTracker(ThreadPoolExecutor executor, int workItemSlotIndex) : base(
                onActionExecuting: workItem =>
                {
                    executor.runningItems[workItemSlotIndex] = workItem;
                },
                onActionExecuted: workItem =>
                {
                    executor.runningItems[workItemSlotIndex] = null;
                })
            {
            }
        }

        private sealed class ExecutorThreadContext
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
    
    internal class WorkItemWrapper
    {
        public static WorkItemWrapper NoOpWorkItemWrapper = new WorkItemWrapper(s => { }, null, TimeSpan.MaxValue);

        public ITimeInterval ExecutionTime { get; private set; }

        private readonly WaitCallback callback;

        private readonly WorkItemStatusProvider statusProvider;

        private readonly TimeSpan executionTimeTreshold;

        private readonly DateTime enqueueTime;

        // for lightweight execution time tracking 
        private DateTime executionStart;

        public WorkItemWrapper(
            WaitCallback callback,
            object state,
            TimeSpan executionTimeTreshold,
            WorkItemStatusProvider statusProvider = null)
        {
            this.callback = callback;
            this.State = state;
            this.executionTimeTreshold = executionTimeTreshold;
            this.statusProvider = statusProvider;
            this.enqueueTime = DateTime.UtcNow;
        }

        internal TimeSpan TimeSinceQueued => Utils.Since(enqueueTime);

        internal object State { get; }

        public void Execute()
        {
            executionStart = DateTime.UtcNow;
            callback.Invoke(State);
        }

        public bool ExecuteWithFilters(IEnumerable<WorkItemFilter> actionFilters)
        {
            return actionFilters.First().ExecuteWorkItem(this);
        }

        //public void Start() // todo
        //{
        //    timeInterval = TimeIntervalFactory.CreateTimeInterval(true);
        //    timeInterval.Start();
        //}

        internal string GetWorkItemStatus(bool detailed)
        {
            return string.Format(
                ThreadPoolExecutor.SR.WorkItem_ExecutionTime, State, Utils.Since(executionStart),
                statusProvider?.Invoke(State, detailed));
        }

        internal bool IsFrozen()
        {
            if (ExecutionTime != null)
            {
                return ExecutionTime.Elapsed > executionTimeTreshold;
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

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking = new ThreadTrackingStatistic(name, null); // todo: null
            }
        }

        public void OnStartExecution()
        {
            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                queueTracking.OnStartExecution();
            }
        }

        public void OnDeQueueRequest(WorkItemWrapper workItem)
        {
            if (ExecutorOptions.CollectDetailedQueueStatistics)
            {
                queueTracking.OnDeQueueRequest(workItem.ExecutionTime);
            }
        }

        public void OnEnQueueRequest(int i, int workQueueCount, WorkItemWrapper workItem)
        {
            if (ExecutorOptions.CollectDetailedQueueStatistics)
            {
                queueTracking.OnEnQueueRequest(i, workQueueCount, workItem.ExecutionTime);
            }
        }

        public void OnStartProcessing()
        {
            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStartProcessing();
            }
        }

        internal void OnStopProcessing()
        {
            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStopProcessing();
            }
        }

        internal void IncrementNumberOfProcessed()
        {
            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.IncrementNumberOfProcessed();
            }
        }

        public void OnStopExecution()
        {
            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStopExecution();
            }
        }
    }
}