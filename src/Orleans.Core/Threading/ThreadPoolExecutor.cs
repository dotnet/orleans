using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
// todo: dependency on runtime (due to logging)
using Orleans.Runtime;

namespace Orleans.Threading
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

        private readonly ILogger log;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));

            workQueue = new BlockingCollection<WorkItemWrapper>(options.PreserveOrder
                ? (IProducerConsumerCollection<WorkItemWrapper>) new ConcurrentQueue<WorkItemWrapper>()
                : new ConcurrentBag<WorkItemWrapper>());

            statistic = new ThreadPoolTrackingStatistic(options.Name, options.LoggerFactory);

            log = options.LoggerFactory.CreateLogger<ThreadPoolExecutor>();

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
            // todo: WorkItem => Action / Runnable? 
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new WorkItemWrapper(callback, state, options.WorkItemExecutionTimeTreshold, options.WorkItemStatusProvider);

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
                    log.Error(
                        ErrorCode.ExecutorTurnTooLong, 
                        string.Format(SR.WorkItem_LongExecutionTime, workItem.GetWorkItemStatus(true)));
                }
            }

            return healthy;
        }

        private void ProcessWorkItems(ExecutorThreadContext threadContext)
        {
            statistic.OnStartExecution();
            try
            {
                while (!workQueue.IsCompleted &&
                       (!options.CancellationToken.IsCancellationRequested || options.DrainAfterCancel))
                {
                    WorkItemWrapper work;
                    try
                    {
                        work = workQueue.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    if (!threadContext.WorkItemFilters.Execute(work))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error(ErrorCode.ExecutorWorkerThreadExc, SR.Executor_Thread_Caugth_Exception, ex);
            }
            finally
            {
                statistic.OnStopExecution();
            }
        }

        private void RunWorker(int threadIndex)
        {
            var threadContext = new ExecutorThreadContext(CreateWorkItemFilters(GetThreadSlotIndex(threadIndex)));
            new ThreadPoolThread(
                    options.Name + threadIndex,
                    options.CancellationToken,
                    options.LoggerFactory,
                    options.FaultHandler)
                .QueueWorkItem(_ => ProcessWorkItems(threadContext));
        }

        private static int GetThreadSlotIndex(int threadIndex)
        {
            // false sharing prevention
            const int padding = 64;
            return threadIndex * padding;
        }

        private WorkItemFiltersApplicant CreateWorkItemFilters(int executorWorkItemSlotIndex)
        {
            return new WorkItemFiltersApplicant(new WorkItemFilter[]
            {
                new OuterExceptionHandler(log),
                new StatisticsTracker(this),
                new RunningWorkItemsTracker(this, executorWorkItemSlotIndex),
                new ExceptionHandler(log)
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
                log.Warn(
                    ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime,
                    SR.Queue_Item_WaitTime, waitTime, workItem.State);
            }

            statistic.OnDeQueueRequest(workItem);
        }

        #endregion

        private sealed class OuterExceptionHandler : WorkItemFilter
        {
            public OuterExceptionHandler(ILogger log) : base(
                exceptionHandler: (ex, workItem) =>
                {
                    if (ex is ThreadAbortException)
                    {
                        if (log.IsEnabled(LogLevel.Debug)) log.Debug(SR.On_Thread_Abort_Exit, ex);
                        Thread.ResetAbort();
                    }
                    else
                    {
                        log.Error(ErrorCode.Runtime_Error_100030, string.Format(SR.Thread_On_Exception, workItem.State), ex);
                    }

                    return false;
                })
            {
            }
        }

        private sealed class StatisticsTracker : WorkItemFilter
        {
            public StatisticsTracker(ThreadPoolExecutor executor) : base(
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

        private sealed class ExceptionHandler : WorkItemFilter
        {
            public ExceptionHandler(ILogger log) : base(
                exceptionHandler: (ex, workItem) =>
                {
                    var tae = ex as ThreadAbortException;
                    if (tae != null)
                    {
                        if (tae.ExceptionState != null && tae.ExceptionState.Equals(true))
                        {
                            Thread.ResetAbort();
                        }
                        else
                        {
                            log.Error(ErrorCode.Runtime_Error_100029, SR.Thread_On_Abort_Propagate, ex);
                        }
                    }
                    else
                    {
                        log.Error(ErrorCode.Runtime_Error_100030, string.Format(SR.Thread_On_Exception, workItem.State), ex);
                    }

                    return true;
                })
            {
            }
        }

        private sealed class ExecutorThreadContext
        {
            public ExecutorThreadContext(WorkItemFiltersApplicant workItemFilters)
            {
                WorkItemFilters = workItemFilters;
            }

            public WorkItemFiltersApplicant WorkItemFilters { get; }
        }

        private sealed class WorkItemFiltersApplicant : ActionFiltersApplicant<WorkItemWrapper>
        {
            public WorkItemFiltersApplicant(IEnumerable<ActionFilter<WorkItemWrapper>> filters) : base(filters)
            {
            }
        }

        internal static class SR
        {
            public const string WorkItem_ExecutionTime = "WorkItem={0} Executing for {1} {2}";

            public const string WorkItem_LongExecutionTime = "Work item {0} has been executing for long time.";

            public const string Executor_Thread_Caugth_Exception = "Executor thread caugth exception:";

            public const string Queue_Item_WaitTime = "Queue wait time of {0} for Item {1}";

            public const string On_Thread_Abort_Exit = "Received thread abort exception - exiting. {0}.";

            public const string Thread_On_Exception = "Thread caught an exception thrown from task {0}.";

            public const string Thread_On_Abort_Propagate = "Caught thread abort exception, allowing it to propagate outwards.";
        }
    }

    internal delegate string WorkItemStatusProvider(object state, bool detailed);

    internal interface IExecutable
    {
        void Execute();
    }

    internal class WorkItemWrapper : IExecutable
    {
        public static WorkItemWrapper NoOpWorkItemWrapper = new WorkItemWrapper(s => { }, null, TimeSpan.MaxValue);

        private readonly WaitCallback callback;

        private readonly WorkItemStatusProvider statusProvider;

        private readonly TimeSpan executionTimeTreshold;

        private readonly DateTime enqueueTime;

        private ITimeInterval executionTime;

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

        // Being tracked only when queue tracking statistic is enabled. Todo: remove implicit behavior?
        internal ITimeInterval ExecutionTime
        {
            get
            {
                EnsureExecutionTime();
                return executionTime;
            }
        }

        internal TimeSpan TimeSinceQueued => Utils.Since(enqueueTime);

        internal object State { get; }

        public void Execute()
        {
            executionStart = DateTime.UtcNow;
            callback.Invoke(State);
        }

        public void EnsureExecutionTime()
        {
            if (executionTime == null)
            {
                executionTime = TimeIntervalFactory.CreateTimeInterval(true);
            }
        }

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

        public ThreadPoolTrackingStatistic(string name, ILoggerFactory loggerFactory)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(name);
            }

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking = new ThreadTrackingStatistic(name, loggerFactory);
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