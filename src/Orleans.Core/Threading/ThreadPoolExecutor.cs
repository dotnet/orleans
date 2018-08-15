using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
// todo: dependency on runtime (due to logging)
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Threading
{
    /// <summary>
    /// Essentially FixedThreadPool with work stealing
    /// </summary>
    internal class ThreadPoolExecutor : IExecutor, IHealthCheckable
    {
        private readonly ThreadPoolWorkQueue workQueue;

        private readonly ThreadPoolExecutorOptions options;
        private readonly SchedulerStatisticsGroup schedulerStatistics;
        private readonly StageAnalysisStatisticsGroup schedulerStageStatistics;
        private readonly IOptions<StatisticsOptions> statisticsOptions;

        private readonly ThreadPoolTrackingStatistic statistic;

        private readonly ExecutingWorkItemsTracker executingWorkTracker;

        private readonly ILogger log;

        public ThreadPoolExecutor(
            ThreadPoolExecutorOptions options,
            SchedulerStatisticsGroup schedulerStatistics,
            StageAnalysisStatisticsGroup schedulerStageStatistics,
            IOptions<StatisticsOptions> statisticsOptions)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.schedulerStatistics = schedulerStatistics;
            this.schedulerStageStatistics = schedulerStageStatistics;
            this.statisticsOptions = statisticsOptions;
            this.workQueue = new ThreadPoolWorkQueue();
            this.statistic = new ThreadPoolTrackingStatistic(options.Name, options.LoggerFactory, statisticsOptions, schedulerStageStatistics);
            this.log = options.LoggerFactory.CreateLogger<ThreadPoolExecutor>();
            this.executingWorkTracker = new ExecutingWorkItemsTracker(options, this.log);
            
            options.CancellationTokenSource.Token.Register(Complete);

            for (var threadIndex = 0; threadIndex < options.DegreeOfParallelism; threadIndex++)
            {
                RunWorker(threadIndex);
            }
        }

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new WorkItem(callback, state, options.WorkItemExecutionTimeTreshold, options.WorkItemStatusProvider);

            statistic.OnEnQueueRequest(workItem);

            workQueue.Enqueue(workItem, forceGlobal: false);
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return !executingWorkTracker.HasFrozenWork();
        }

        public void Complete()
        {
            workQueue.CompleteAdding();
        }

        private void ProcessWorkItems(ExecutionContext context)
        {
            var threadLocals = workQueue.EnsureCurrentThreadHasQueue();
            statistic.OnStartExecution();
            try
            {
                while (!ShouldStop())
                {
                    while (workQueue.TryDequeue(threadLocals, out var workItem))
                    {
                        if (ShouldStop())
                        {
                            return;
                        }

                        context.ExecuteWithFilters(workItem);
                    }

                    workQueue.WaitForWork();
                }
            }
            catch (Exception ex)
            {
                if (ex is ThreadAbortException)
                {
                    return;
                }

                log.Error(ErrorCode.ExecutorProcessingError, string.Format(SR.Executor_On_Exception, options.Name), ex);
            }
            finally
            {
                statistic.OnStopExecution();
            }

            bool ShouldStop()
            {
                return context.CancellationTokenSource.IsCancellationRequested && !options.DrainAfterCancel;
            }
        }

        private void RunWorker(int index)
        {
            var actionFilters = new ActionFilter<ExecutionContext>[]
            {
                new StatisticsTracker(statistic, options.DelayWarningThreshold, log, this.schedulerStatistics),
                executingWorkTracker
            }.Union(options.ExecutionFilters);

            var exceptionFilters = new[] { new ThreadAbortHandler(log) }.Union(options.ExceptionFilters);

            var context = new ExecutionContext(
                actionFilters,
                exceptionFilters,
                options.CancellationTokenSource,
                index);

            new ThreadPoolThread(
                    options.Name + index,
                    options.CancellationTokenSource.Token,
                    options.LoggerFactory,
                    this.statisticsOptions,
                    this.schedulerStageStatistics)
                .QueueWorkItem(_ => ProcessWorkItems(context));
        }

        private sealed class ThreadAbortHandler : ExecutionExceptionFilter
        {
            private readonly ILogger log;

            public ThreadAbortHandler(ILogger log)
            {
                this.log = log;
            }

            public override bool ExceptionHandler(Exception ex, ExecutionContext context)
            {
                if (!(ex is ThreadAbortException))
                {
                    return false;
                }

                if (log.IsEnabled(LogLevel.Debug)) log.Debug(SR.On_Thread_Abort_Exit, ex);
                Thread.ResetAbort();
                context.CancellationTokenSource.Cancel();
                return true;
            }
        }

        private sealed class StatisticsTracker : ExecutionActionFilter
        {
            private readonly ThreadPoolTrackingStatistic statistic;

            private readonly TimeSpan delayWarningThreshold;

            private readonly ILogger log;
            private readonly SchedulerStatisticsGroup schedulerStatistics;

            public StatisticsTracker(ThreadPoolTrackingStatistic statistic, TimeSpan delayWarningThreshold, ILogger log, SchedulerStatisticsGroup schedulerStatistics)
            {
                this.statistic = statistic;
                this.delayWarningThreshold = delayWarningThreshold;
                this.log = log;
                this.schedulerStatistics = schedulerStatistics;
            }

            public override void OnActionExecuting(ExecutionContext context)
            {
                TrackRequestDequeue(context.WorkItem);
                statistic.OnStartProcessing();
            }

            public override void OnActionExecuted(ExecutionContext context)
            {
                statistic.OnStopProcessing();
                statistic.IncrementNumberOfProcessed();
            }

            private void TrackRequestDequeue(WorkItem workItem)
            {
                var waitTime = workItem.TimeSinceQueued;
                if (waitTime > delayWarningThreshold && !System.Diagnostics.Debugger.IsAttached && workItem.State != null)
                {
                    this.schedulerStatistics.NumLongQueueWaitTimes.Increment();
                    log.Warn(ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime, SR.Queue_Item_WaitTime, waitTime, workItem.State);
                }

                statistic.OnDeQueueRequest(workItem);
            }
        }

        private sealed class ExecutingWorkItemsTracker : ExecutionActionFilter
        {
            private readonly WorkItem[] runningItems;

            private readonly ILogger log;

            public ExecutingWorkItemsTracker(ThreadPoolExecutorOptions options, ILogger log)
            {
                if (options == null) throw new ArgumentNullException(nameof(options));
                this.runningItems = new WorkItem[GetThreadSlot(options.DegreeOfParallelism)];
                this.log = log ?? throw new ArgumentNullException(nameof(log));
            }

            public override void OnActionExecuting(ExecutionContext context)
            {
                runningItems[GetThreadSlot(context.ThreadIndex)] = context.WorkItem;
            }

            public override void OnActionExecuted(ExecutionContext context)
            {
                runningItems[GetThreadSlot(context.ThreadIndex)] = null;
            }

            public bool HasFrozenWork()
            {
                var frozen = false;
                foreach (var workItem in runningItems)
                {
                    if (workItem != null && workItem.IsFrozen())
                    {
                        frozen = true;
                        this.log.Error(
                            ErrorCode.ExecutorTurnTooLong,
                            string.Format(SR.WorkItem_LongExecutionTime, workItem.GetWorkItemStatus(true)));
                    }
                }

                return frozen;
            }

            private static int GetThreadSlot(int threadIndex)
            {
                // false sharing prevention
                const int cacheLineSize = 64;
                const int padding = cacheLineSize;
                return threadIndex * padding;
            }
        }

        internal static class SR
        {
            public const string WorkItem_ExecutionTime = "WorkItem={0} Executing for {1} {2}";

            public const string WorkItem_LongExecutionTime = "Work item {0} has been executing for long time.";

            public const string Queue_Item_WaitTime = "Queue wait time of {0} for Item {1}";

            public const string On_Thread_Abort_Exit = "Received thread abort exception - exiting. {0}.";

            public const string Executor_On_Exception = "Executor {0} caught an exception.";
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

        private readonly StatusProvider statusProvider;

        private readonly TimeSpan executionTimeTreshold;

        private readonly DateTime enqueueTime;

        private ITimeInterval executionTime;

        public WorkItem(
            WaitCallback callback,
            object state,
            TimeSpan executionTimeTreshold,
            StatusProvider statusProvider = null)
        {
            this.callback = callback;
            this.State = state;
            this.executionTimeTreshold = executionTimeTreshold;
            this.statusProvider = statusProvider ?? NoOpStatusProvider;
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
            return Utils.Since(ExecutionStart) > executionTimeTreshold;
        }

        internal delegate string StatusProvider(object state, bool detailed);

        private static readonly StatusProvider NoOpStatusProvider = (s, d) => string.Empty;
    }

    internal class ExecutionContext : IExecutable
    {
        private readonly FiltersApplicant<ExecutionContext> filtersApplicant;

        public ExecutionContext(
            IEnumerable<ActionFilter<ExecutionContext>> actionFilters,
            IEnumerable<ExceptionFilter<ExecutionContext>> exceptionFilters,
            CancellationTokenSource cts,
            int threadIndex)
        {
            filtersApplicant = new FiltersApplicant<ExecutionContext>(actionFilters, exceptionFilters);
            CancellationTokenSource = cts;
            ThreadIndex = threadIndex;
        }

        public CancellationTokenSource CancellationTokenSource { get; }

        public WorkItem WorkItem { get; private set; }

        internal int ThreadIndex { get; }

        public void ExecuteWithFilters(WorkItem workItem)
        {
            WorkItem = workItem;

            try
            {
                filtersApplicant.Apply(this);
            }
            finally
            {
                Reset();
            }
        }

        public void Execute()
        {
            WorkItem.Execute();
        }

        private void Reset()
        {
            WorkItem = null;
        }
    }

    internal class ThreadPoolTrackingStatistic
    {
        private readonly ThreadTrackingStatistic threadTracking;

        private readonly QueueTrackingStatistic queueTracking;
        private readonly StatisticsLevel statisticsLevel;

        public ThreadPoolTrackingStatistic(string name, ILoggerFactory loggerFactory, IOptions<StatisticsOptions> statisticsOptions, StageAnalysisStatisticsGroup schedulerStageStatistics)
        {
            this.statisticsLevel = statisticsOptions.Value.CollectionLevel;
            if (statisticsLevel.CollectQueueStats())
            {
                queueTracking = new QueueTrackingStatistic(name, statisticsOptions);
            }

            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                threadTracking = new ThreadTrackingStatistic(name, loggerFactory, statisticsOptions, schedulerStageStatistics);
            }
        }

        public void OnStartExecution()
        {
            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                queueTracking.OnStartExecution();
            }
        }

        public void OnDeQueueRequest(WorkItem workItem)
        {
            if (this.statisticsLevel.CollectDetailedQueueStatistics())
            {
                queueTracking.OnDeQueueRequest(workItem.ExecutionTime);
            }
        }

        public void OnEnQueueRequest(WorkItem workItem)
        {
            if (this.statisticsLevel.CollectDetailedQueueStatistics())
            {
                queueTracking.OnEnQueueRequest(1, queueLength: 0, itemInQueue: workItem.ExecutionTime);
            }
        }

        public void OnStartProcessing()
        {
            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                threadTracking.OnStartProcessing();
            }
        }

        internal void OnStopProcessing()
        {
            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                threadTracking.OnStopProcessing();
            }
        }

        internal void IncrementNumberOfProcessed()
        {
            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                threadTracking.IncrementNumberOfProcessed();
            }
        }

        public void OnStopExecution()
        {
            if (this.statisticsLevel.CollectDetailedThreadStatistics())
            {
                threadTracking.OnStopExecution();
            }
        }
    }
}
