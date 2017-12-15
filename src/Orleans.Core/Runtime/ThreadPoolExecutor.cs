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
#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif
        private readonly QueueTrackingStatistic queueTracking;

        private readonly QueueWorkItemCallback[] runningWorkItems;

        private readonly BlockingCollection<QueueWorkItemCallback> workQueue;

        private readonly ThreadPoolExecutorOptions executorOptions;

        public ThreadPoolExecutor(ThreadPoolExecutorOptions options)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(options.Name);
            }

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
            workQueue = new BlockingCollection<QueueWorkItemCallback>(options.PreserveOrder ?
                (IProducerConsumerCollection<QueueWorkItemCallback>)new ConcurrentQueue<QueueWorkItemCallback>() :
                new ConcurrentBag<QueueWorkItemCallback>());

            executorOptions = options;
            executorOptions.CancellationToken.Register(() =>
            {
                var chanceToGracefullyExit = QueueWorkItemCallback.NoOpQueueWorkItemCallback;
                workQueue.Add(chanceToGracefullyExit);
                workQueue.CompleteAdding();
            });

            // padding reduces false sharing
            const int padding = 64;
            runningWorkItems = new QueueWorkItemCallback[options.DegreeOfParallelism * padding];
            for (var threadIndex = 0; threadIndex < options.DegreeOfParallelism; threadIndex++)
            {
                var workItemSlotIndex = threadIndex * padding;
                var threadContext = new ExecutorThreadContext(CreateWorkItemFilters(workItemSlotIndex), workItemSlotIndex);
                new ThreadPerTaskExecutor(
                    new SingleThreadExecutorOptions(
                        options.Name + threadIndex,
                        options.StageType,
                        options.CancellationToken,
                        options.Log,
                        options.FaultHandler))
                    .QueueWorkItem(_ => ProcessQueue(threadContext));
            }
        }

        public int WorkQueueCount => workQueue.Count;

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var workItem = new QueueWorkItemCallback(
                callback,
                state,
                executorOptions.WorkItemExecutionTimeTreshold,
                executorOptions.WorkItemStatusProvider);

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
                    executorOptions.Log.Error(ErrorCode.SchedulerTurnTooLong,
                        $"Work item {workItem.GetWorkItemStatus(true)} has been executing for long time.");
                }
            }

            return healthy;
        }

        protected void ProcessQueue(ExecutorThreadContext threadContext)
        {
            TrackExecutionStart();

            try
            {
                RunNonBatchingV2(threadContext);
            }
            finally
            {
                TrackExecutionStop();
            }
        }

        protected void RunNonBatching(ExecutorThreadContext threadContext)
        {
            try
            {
                while (!workQueue.IsCompleted &&
                       (!executorOptions.CancellationToken.IsCancellationRequested || executorOptions.DrainAfterCancel))
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

                    try
                    {
                        runningWorkItems[threadContext.WorkItemSlotIndex] = workItem;
                        TrackRequestDequeue(workItem);
                        TrackProcessingStart();
                        try
                        {
                            workItem.Execute();
                        }
                        catch (ThreadAbortException ex)
                        {
                            // The current turn was aborted (indicated by the exception state being set to true).
                            // In this case, we just reset the abort so that life continues. No need to do anything else.
                            if ((ex.ExceptionState != null) && ex.ExceptionState.Equals(true))
                                Thread.ResetAbort();
                            else
                                executorOptions.Log.Error(ErrorCode.Runtime_Error_100029,
                                    "Caught thread abort exception, allowing it to propagate outwards", ex);
                        }
                        catch (Exception ex)
                        {
                            executorOptions.Log
                                .Error(ErrorCode.Runtime_Error_100030, $"Worker thread caught an exception thrown from task {workItem.State}.", ex);
                        }
                        finally
                        {
#if TRACK_DETAILED_STATS
                                // todo
                                if (todo.ItemType != WorkItemType.WorkItemGroup)
                                {
                                    if (StatisticsCollector.CollectTurnsStats)
                                    {
                                        //SchedulerStatisticsGroup.OnTurnExecutionEnd(CurrentStateTime.Elapsed);
                                        SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(CurrentStateStarted));
                                    }
                                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                    {
                                        threadTracking.IncrementNumberOfProcessed();
                                    }
                                    CurrentWorkItem = null;
                                }
                                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                {
                                    threadTracking.OnStopProcessing();
                                }
#endif
                        }

                        TrackProcessingStop();
                        runningWorkItems[threadContext.WorkItemSlotIndex] = null;
                    }
                    catch (ThreadAbortException tae)
                    {
                        // Can be reported from RunQueue.Get when Silo is being shutdown, so downgrade to verbose log
                        if (executorOptions.Log.IsEnabled(LogLevel.Debug)) executorOptions.Log.Debug("Received thread abort exception -- exiting. {0}", tae);
                        Thread.ResetAbort();
                        break;
                    }
                    catch (Exception ex)
                    {
                        executorOptions.Log.Error(ErrorCode.Runtime_Error_100031, "Exception bubbled up to worker thread", ex);
                        break;
                    }
                }
            }
            catch (Exception exc)
            {
                executorOptions.Log.Error(ErrorCode.SchedulerWorkerThreadExc, "WorkerPoolThread caugth exception:", exc);
            }
        }

        protected void RunNonBatchingV2(ExecutorThreadContext threadContext)
        {
            try
            {
                while (!workQueue.IsCompleted &&
                       (!executorOptions.CancellationToken.IsCancellationRequested || executorOptions.DrainAfterCancel))
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

                    if (!ExecuteWorkItem(workItem, threadContext.WorkItemFilters))
                    {
                        break;
                    }
                }
            }
            catch (Exception exc)
            {
                executorOptions.Log.Error(ErrorCode.SchedulerWorkerThreadExc, "Executor thread caugth exception:", exc);
            }
        }

        private bool ExecuteWorkItem(QueueWorkItemCallback workItem, IEnumerable<WorkItemFilter> actionFilters = null)
        {
           return (actionFilters?.FirstOrDefault() ?? NoOpWorkItemFilter.Instance)
                .ExecuteWorkItem(workItem);
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
            // Capture the queue wait time for this task
            var waitTime = workItem.TimeSinceQueued;
            if (waitTime > executorOptions.DelayWarningThreshold && !System.Diagnostics.Debugger.IsAttached)
            {
                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                executorOptions.Log.Warn(
                    ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime,
                    "Queue wait time of {0} for Item {1}", waitTime, workItem.State);
            }

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

        private WorkItemFilter[] CreateWorkItemFilters(int executorWorkItemSlotIndex)
        {
            return WorkItemFilter.CreateChain(new Func<WorkItemFilter>[]
            {
                () => new OuterExceptionHandlerFilter(executorOptions.Log),
                () => new StatisticsTrackingFilter(this),
                () => new RunningWorkItemsTrackerFilter(this, executorWorkItemSlotIndex),
                () => new ExceptionHandlerFilter(executorOptions.Log)
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

                onActionExecuted: workItem =>
                {
                    executor.TrackProcessingStop();
                })
            {
            }
        }

        private sealed class RunningWorkItemsTrackerFilter : WorkItemFilter
        {
            public RunningWorkItemsTrackerFilter(ThreadPoolExecutor executor, int workItemSlotIndex) : base(
                onActionExecuting: workItem =>
                {
                    executor.runningWorkItems[workItemSlotIndex] = workItem;
                },

                onActionExecuted: workItem =>
                {
                    executor.runningWorkItems[workItemSlotIndex] = null;
                })
            {
            }
        }

        private sealed class NoOpWorkItemFilter : WorkItemFilter
        {
            private NoOpWorkItemFilter() { }

            public static readonly NoOpWorkItemFilter Instance = new NoOpWorkItemFilter();
        }

        internal sealed class ExecutorThreadContext
        {
            public ExecutorThreadContext(WorkItemFilter[] workItemFilters, int workItemSlotIndex)
            {
                WorkItemFilters = workItemFilters;
                WorkItemSlotIndex = workItemSlotIndex;
            }

            public WorkItemFilter[] WorkItemFilters { get; }

            // todo: to be removed
            public int WorkItemSlotIndex { get; }
        }
    }

    internal delegate string WorkItemStatusProvider(object state, bool detailed);


    internal class QueueWorkItemCallback : ITimeInterval
    {
        public static QueueWorkItemCallback NoOpQueueWorkItemCallback = new QueueWorkItemCallback(s => { }, null, TimeSpan.MaxValue);

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

        internal bool IsFrozen()
        {
            if (timeInterval != null)
            {
                return timeInterval.Elapsed > executionTimeTreshold;
            }

            return false;
        }
    }
}