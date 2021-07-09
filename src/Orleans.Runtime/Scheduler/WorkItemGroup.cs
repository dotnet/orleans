using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Scheduler
{
    [DebuggerDisplay("WorkItemGroup Name={Name} State={state}")]
    internal class WorkItemGroup : IWorkItem, IDisposable, IWorkItemScheduler
    {
        private enum WorkGroupStatus
        {
            Waiting = 0,
            Runnable = 1,
            Running = 2
        }

        private readonly ILogger log;
        private WorkGroupStatus state;
        private readonly object lockable;
        private readonly Queue<Task> workItems;

        private long totalItemsEnqueued;
        private long totalItemsProcessed;
        private long lastLongQueueWarningTimestamp;

        private Task currentTask;
        private long currentTaskStarted;

        private readonly QueueTrackingStatistic queueTracking;
        private readonly int workItemGroupStatisticsNumber;
        private readonly SchedulingOptions schedulingOptions;
        private readonly SchedulerStatisticsGroup schedulerStatistics;

        internal ActivationTaskScheduler TaskScheduler { get; } 

        public IGrainContext GrainContext { get; set; }

        internal bool IsSystemGroup => this.GrainContext is ISystemTargetBase;

        public string Name => GrainContext?.ToString() ?? "Unknown";

        internal int ExternalWorkItemCount
        {
            get { lock (lockable) { return WorkItemCount; } }
        }

        private Task CurrentTask
        {
            get => currentTask;
            set
            {
                currentTask = value;
                currentTaskStarted = Environment.TickCount64;
            }
        }

        private int WorkItemCount => workItems.Count;

        public WorkItemGroup(
            IGrainContext grainContext,
            ILogger<WorkItemGroup> logger,
            ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
            SchedulerStatisticsGroup schedulerStatistics,
            IOptions<StatisticsOptions> statisticsOptions,
            IOptions<SchedulingOptions> schedulingOptions)
        {
            GrainContext = grainContext;
            this.schedulingOptions = schedulingOptions.Value;
            this.schedulerStatistics = schedulerStatistics;
            state = WorkGroupStatus.Waiting;
            workItems = new Queue<Task>();
            lockable = new object();
            totalItemsEnqueued = 0;
            totalItemsProcessed = 0;
            TaskScheduler = new ActivationTaskScheduler(this, activationTaskSchedulerLogger);
            log = logger;

            if (schedulerStatistics.CollectShedulerQueuesStats)
            {
                queueTracking = new QueueTrackingStatistic("Scheduler." + this.Name, statisticsOptions);
                queueTracking.OnStartExecution();
            }

            if (schedulerStatistics.CollectPerWorkItemStats)
            {
                workItemGroupStatisticsNumber = schedulerStatistics.RegisterWorkItemGroup(this.Name, this.GrainContext,
                    () =>
                    {
                        var sb = new StringBuilder();
                        lock (lockable)
                        {

                            sb.Append("QueueLength = " + WorkItemCount);
                            sb.Append($", State = {state}");
                            if (state == WorkGroupStatus.Runnable)
                                sb.Append($"; oldest item is {(workItems.Count >= 0 ? workItems.Peek().ToString() : "null")} old");
                        }
                        return sb.ToString();
                    });
            }
        }

        /// <summary>
        /// Adds a task to this activation.
        /// If we're adding it to the run list and we used to be waiting, now we're runnable.
        /// </summary>
        /// <param name="task">The work item to add.</param>
        public void EnqueueTask(Task task)
        {
#if DEBUG
            if (log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace(
                    "EnqueueWorkItem {Task} into {GrainContext} when TaskScheduler.Current={TaskScheduler}",
                    task,
                    this.GrainContext,
                    System.Threading.Tasks.TaskScheduler.Current);
            }
#endif

            lock (lockable)
            {
                long thisSequenceNumber = totalItemsEnqueued++;
                int count = WorkItemCount;

                workItems.Enqueue(task);
                int maxPendingItemsLimit = schedulingOptions.MaxPendingWorkItemsSoftLimit;
                if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
                {
                    var now = ValueStopwatch.GetTimestamp();
                    if (ValueStopwatch.FromTimestamp(this.lastLongQueueWarningTimestamp, now).Elapsed > TimeSpan.FromSeconds(10))
                    {
                        log.LogWarning(
                            (int)ErrorCode.SchedulerTooManyPendingItems,
                            "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}",
                            count,
                            this.Name,
                            maxPendingItemsLimit);
                    }

                    lastLongQueueWarningTimestamp = now;
                }
                if (state != WorkGroupStatus.Waiting) return;

                state = WorkGroupStatus.Runnable;
#if DEBUG
                if (log.IsEnabled(LogLevel.Trace))
                {
                    log.LogTrace(
                        "Add to RunQueue {Task}, #{SequenceNumber}, onto {GrainContext}",
                        task,
                        thisSequenceNumber,
                        GrainContext);
                }
#endif
                ScheduleExecution(this);
            }
        }

        /// <summary>
        /// For debugger purposes only.
        /// </summary>
        internal IEnumerable<Task> GetScheduledTasks()
        {
            foreach (var task in this.workItems)
            {
                yield return task;
            }
        }

        private static object DumpAsyncState(object o)
        {
            if (o is Delegate action)
                return action.Target is null ? action.Method.DeclaringType + "." + action.Method.Name
                    : action.Method.DeclaringType.Name + "." + action.Method.Name + ": " + DumpAsyncState(action.Target);

            if (o?.GetType() is { Name: "ContinuationWrapper" } wrapper
                && (wrapper.GetField("_continuation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? wrapper.GetField("m_continuation", BindingFlags.Instance | BindingFlags.NonPublic)
                    )?.GetValue(o) is Action continuation)
                return DumpAsyncState(continuation);

            return o;
        }

        // Execute one or more turns for this activation. 
        // This method is always called in a single-threaded environment -- that is, no more than one
        // thread will be in this method at once -- but other asynch threads may still be queueing tasks, etc.
        public void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.GrainContext);

                // Process multiple items -- drain the applicationMessageQueue (up to max items) for this physical activation
                int count = 0;
                var stopwatch = ValueStopwatch.StartNew();
                do
                {
                    lock (lockable)
                    {
                        state = WorkGroupStatus.Running;
                    }

                    // Get the first Work Item on the list
                    Task task;
                    lock (lockable)
                    {
                        if (workItems.Count > 0)
                            CurrentTask = task = workItems.Dequeue();
                        else // If the list is empty, then we're done
                            break;
                    }

#if DEBUG
                    if (log.IsEnabled(LogLevel.Trace))
                    {
                        log.LogTrace(
                        "About to execute task {Task} in GrainContext={GrainContext}",
                        OrleansTaskExtentions.ToString(task),
                        this.GrainContext);
                    }
#endif
                    var taskStart = stopwatch.Elapsed;

                    try
                    {
                        TaskScheduler.RunTask(task);
                    }
                    catch (Exception ex)
                    {
                        this.log.LogError(
                            (int)ErrorCode.SchedulerExceptionFromExecute,
                            ex,
                            "Worker thread caught an exception thrown from Execute by task {Task}. Exception: {Exception}",
                            OrleansTaskExtentions.ToString(task),
                            ex);
                        throw;
                    }
                    finally
                    {
                        totalItemsProcessed++;
                        var taskLength = stopwatch.Elapsed - taskStart;
                        if (taskLength > schedulingOptions.TurnWarningLengthThreshold)
                        {
                            this.schedulerStatistics.NumLongRunningTurns.Increment();
                            this.log.LogWarning(
                                (int)ErrorCode.SchedulerTurnTooLong3,
                                "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}",
                                OrleansTaskExtentions.ToString(task),
                                this.GrainContext.ToString(),
                                taskLength.ToString("g"),
                                schedulingOptions.TurnWarningLengthThreshold,
                                Thread.CurrentThread.ManagedThreadId.ToString());
                        }

                        CurrentTask = null;
                    }
                    count++;
                }
                while (schedulingOptions.ActivationSchedulingQuantum <= TimeSpan.Zero || stopwatch.Elapsed < schedulingOptions.ActivationSchedulingQuantum);
            }
            catch (Exception ex)
            {
                this.log.LogError(
                    (int)ErrorCode.Runtime_Error_100032,
                    ex,
                    "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute: {Exception}",
                    Thread.CurrentThread.ManagedThreadId,
                    ex);
            }
            finally
            {
                // Now we're not Running anymore. 
                // If we left work items on our run list, we're Runnable, and need to go back on the silo run queue; 
                // If our run list is empty, then we're waiting.
                lock (lockable)
                {
                    if (WorkItemCount > 0)
                    {
                        state = WorkGroupStatus.Runnable;
                        ScheduleExecution(this);
                    }
                    else
                    {
                        state = WorkGroupStatus.Waiting;
                    }
                }

                RuntimeContext.ResetExecutionContext();
            }
        }

        public override string ToString() => $"{(IsSystemGroup ? "System*" : "")}WorkItemGroup:Name={Name},WorkGroupStatus={state}";

        public string DumpStatus()
        {
            lock (lockable)
            {
                var sb = new StringBuilder();
                sb.Append(this);
                sb.AppendFormat(". Currently QueuedWorkItems={0}; Total Enqueued={1}; Total processed={2}; ",
                    WorkItemCount, totalItemsEnqueued, totalItemsProcessed);
                if (CurrentTask is Task task)
                {
                    sb.AppendFormat(" Executing Task Id={0} Status={1} for {2}.",
                        task.Id, task.Status, TimeSpan.FromMilliseconds(Environment.TickCount64 - currentTaskStarted));
                }

                sb.AppendFormat("TaskRunner={0}; ", TaskScheduler);
                if (GrainContext != null)
                {
                    var detailedStatus = this.GrainContext switch
                    {
                        ActivationData activationData => activationData.ToDetailedString(includeExtraDetails: true),
                        SystemTarget systemTarget => systemTarget.ToDetailedString(),
                        object obj => obj.ToString(),
                        _ => "None"
                    };
                    sb.AppendFormat("Detailed context=<{0}>", detailedStatus);
                }
                return sb.ToString();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ScheduleExecution(WorkItemGroup workItem)
        {
            ThreadPool.UnsafeQueueUserWorkItem(workItem, preferLocal: true);
        }

        public void Dispose()
        {
            this.schedulerStatistics.UnregisterWorkItemGroup(workItemGroupStatisticsNumber);
        }

        public void QueueAction(Action action) => TaskScheduler.QueueAction(action);
        public void QueueTask(Task task) => task.Start(TaskScheduler);
        public void QueueWorkItem(IThreadPoolWorkItem workItem) => TaskScheduler.QueueThreadPoolWorkItem(workItem);
    }
}
