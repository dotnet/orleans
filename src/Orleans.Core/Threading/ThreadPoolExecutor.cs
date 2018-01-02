﻿using System;
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
        private readonly BlockingCollection<WorkItem> workQueue;

        private readonly ThreadPoolExecutorOptions options;

        private readonly ThreadPoolTrackingStatistic statistic;

        private readonly ExecutingWorkItemsTracker executingWorkTracker;

        private readonly ILogger log;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));

            workQueue = new BlockingCollection<WorkItem>(options.PreserveOrder
                ? (IProducerConsumerCollection<WorkItem>) new ConcurrentQueue<WorkItem>()
                : new ConcurrentBag<WorkItem>());

            statistic = new ThreadPoolTrackingStatistic(options.Name, options.LoggerFactory);

            executingWorkTracker = new ExecutingWorkItemsTracker(this);

            log = options.LoggerFactory.CreateLogger<ThreadPoolExecutor>();

            options.CancellationTokenSource.Token.Register(() =>
            {
                var chanceToGracefullyExit = WorkItem.NoOp;
                workQueue.Add(chanceToGracefullyExit);
                workQueue.CompleteAdding();
            });

            for (var threadIndex = 0; threadIndex < options.DegreeOfParallelism; threadIndex++)
            {
                RunWorker(new ExecutionContext(CreateWorkItemFilters(), options.CancellationTokenSource, GetThreadSlotIndex(threadIndex)));
            }
        }

        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            // todo: WorkItem => Action / Runnable? 
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new WorkItem(callback, state, options.WorkItemExecutionTimeTreshold, options.WorkItemStatusProvider);

            TrackRequestEnqueue(workItem);

            workQueue.Add(workItem);
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return !executingWorkTracker.HasFrozenWork();
        }

        private void ProcessWorkItems(ExecutionContext context)
        {
            statistic.OnStartExecution();
            try
            {
                while (!workQueue.IsCompleted && (!context.CancellationTokenSource.IsCancellationRequested || options.DrainAfterCancel))
                {
                    try
                    {
                        context.WorkItem = workQueue.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    try
                    {
                        context.Execute();
                    }
                    finally
                    {
                        context.Reset();
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

        private void RunWorker(ExecutionContext context)
        {
            new ThreadPoolThread(
                    options.Name + context.ThreadSlot,
                    options.CancellationTokenSource.Token,
                    options.LoggerFactory,
                    options.FaultHandler)
                .QueueWorkItem(_ => ProcessWorkItems(context));
        }

        private WorkItemFiltersApplicant CreateWorkItemFilters()
        {
            return new WorkItemFiltersApplicant(new ExecutionFilter[]
            {
                new OuterExceptionHandler(log),
                new StatisticsTracker(this),
                new ExecutingWorkItemsTracker(this),
                new ExceptionHandler(log)
            }.Union(options.ExecutionFilters ?? Array.Empty<ExecutionFilter>()));
        }

        private static int GetThreadSlotIndex(int threadIndex)
        {
            // false sharing prevention
            const int padding = 64;
            return threadIndex * padding;
        }

        #region StatisticsTracking

        private void TrackRequestEnqueue(WorkItem workItem)
        {
            statistic.OnEnQueueRequest(1, WorkQueueCount, workItem);
        }

        private void TrackRequestDequeue(WorkItem workItem)
        {
            var waitTime = workItem.TimeSinceQueued;
            if (waitTime > options.DelayWarningThreshold && !System.Diagnostics.Debugger.IsAttached)
            {
                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                log.Warn(ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime, SR.Queue_Item_WaitTime, waitTime, workItem.State);
            }

            statistic.OnDeQueueRequest(workItem);
        }

        #endregion

        private sealed class OuterExceptionHandler : ExecutionFilter
        {
            public OuterExceptionHandler(ILogger log) : base(
                exceptionHandler: (ex, context) =>
                {
                    if (ex is ThreadAbortException)
                    {
                        if (log.IsEnabled(LogLevel.Debug)) log.Debug(SR.On_Thread_Abort_Exit, ex);
                        Thread.ResetAbort();
                    }
                    else
                    {
                        log.Error(ErrorCode.Runtime_Error_100030, string.Format(SR.Thread_On_Exception, context.WorkItem.State), ex);
                    }

                    return false;
                })
            {
            }
        }

        private sealed class StatisticsTracker : ExecutionFilter
        {
            public StatisticsTracker(ThreadPoolExecutor executor) : base(
                onActionExecuting: context =>
                {
                    executor.TrackRequestDequeue(context.WorkItem);
                    executor.statistic.OnStartProcessing();
                },
                onActionExecuted: context =>
                {
                    executor.statistic.OnStopProcessing();
                    executor.statistic.IncrementNumberOfProcessed();
                })
            {
            }
        }

        private sealed class ExecutingWorkItemsTracker : ExecutionFilter
        {
            private readonly WorkItem[] runningItems;

            private readonly ILogger log;

            public ExecutingWorkItemsTracker(ThreadPoolExecutor executor)
            {
                runningItems = new WorkItem[GetThreadSlotIndex(executor.options.DegreeOfParallelism)];
                log = executor.log;
            }

            public override Action<ExecutionContext> OnActionExecuting => context => runningItems[context.ThreadSlot] = context.WorkItem;

            public override Action<ExecutionContext> OnActionExecuted => context => runningItems[context.ThreadSlot] = null;

            public bool HasFrozenWork()
            {
                var frozen = false;
                foreach (var workItem in runningItems)
                {
                    if (workItem != null && workItem.IsFrozen())
                    {
                        frozen = true;
                        log.Error(
                            ErrorCode.ExecutorTurnTooLong,
                            string.Format(SR.WorkItem_LongExecutionTime, workItem.GetWorkItemStatus(true)));
                    }
                }

                return frozen;
            }
        }

        private sealed class ExceptionHandler : ExecutionFilter
        {
            public ExceptionHandler(ILogger log) : base(
                exceptionHandler: (ex, context) =>
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
                        log.Error(ErrorCode.Runtime_Error_100030, string.Format(SR.Thread_On_Exception, context.WorkItem.State), ex);
                    }

                    return true;
                })
            {
            }
        }

        internal sealed class WorkItemFiltersApplicant : ActionFiltersApplicant<ExecutionContext>
        {
            public WorkItemFiltersApplicant(IEnumerable<ExecutionFilter> filters) : base(filters)
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

    internal interface IExecutable
    {
        void Execute();
    }

    internal class WorkItem : IExecutable
    {
        public static WorkItem NoOp = new WorkItem(s => { }, null, TimeSpan.MaxValue);

        private readonly WaitCallback callback;

        private readonly WorkItemStatusProvider statusProvider;

        private readonly TimeSpan executionTimeTreshold;

        private readonly DateTime enqueueTime;

        private ITimeInterval executionTime;

        public WorkItem(
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

        // for lightweight execution time tracking 
        public DateTime ExecutionStart { get; private set; }

        internal TimeSpan TimeSinceQueued => Utils.Since(enqueueTime);

        internal object State { get; }

        public void Execute()
        {
            ExecutionStart = DateTime.UtcNow;
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
                ThreadPoolExecutor.SR.WorkItem_ExecutionTime, State, Utils.Since(ExecutionStart),
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

    internal class ExecutionContext : IExecutable
    {
        public ExecutionContext(ThreadPoolExecutor.WorkItemFiltersApplicant workItemFiltersApplicant, CancellationTokenSource cts, int threadSlot)
        {
            WorkItemFiltersApplicant = workItemFiltersApplicant;
            CancellationTokenSource = cts;
            ThreadSlot = threadSlot;
        }

        public ThreadPoolExecutor.WorkItemFiltersApplicant WorkItemFiltersApplicant { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public WorkItem WorkItem { get; set; }

        internal int ThreadSlot { get; }

        public void Execute()
        {
            WorkItem.Execute();
        }

        public void Reset()
        {
            WorkItem = null;
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

        public void OnDeQueueRequest(WorkItem workItem)
        {
            if (ExecutorOptions.CollectDetailedQueueStatistics)
            {
                queueTracking.OnDeQueueRequest(workItem.ExecutionTime);
            }
        }

        public void OnEnQueueRequest(int i, int workQueueCount, WorkItem workItem)
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

    internal delegate string WorkItemStatusProvider(object state, bool detailed);
}