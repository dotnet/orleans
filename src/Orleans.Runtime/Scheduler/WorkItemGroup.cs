using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// Shared state for <see cref="WorkItemGroup"/>.
    /// </summary>
    internal sealed class WorkItemGroupShared
    {
        public WorkItemGroupShared(
            ILogger<WorkItemGroup> logger,
            ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
            SchedulerStatisticsGroup schedulerStatistics,
            IOptions<SchedulingOptions> schedulingOptions)
        {
            SchedulingOptions = schedulingOptions.Value;
            ActivationSchedulingQuantumMillis = (long)SchedulingOptions.ActivationSchedulingQuantum.TotalMilliseconds;
            TurnWarningLengthThresholdMilliseconds = (long)SchedulingOptions.TurnWarningLengthThreshold.TotalMilliseconds;
            ActivationTaskSchedulerLogger = activationTaskSchedulerLogger;
            Logger = logger;
            SchedulerStatistics = schedulerStatistics;
        }

        public SchedulingOptions SchedulingOptions { get; }
        public long ActivationSchedulingQuantumMillis { get; }
        public long TurnWarningLengthThresholdMilliseconds { get; }
        public ILogger<ActivationTaskScheduler> ActivationTaskSchedulerLogger { get; }
        public ILogger<WorkItemGroup> Logger { get; }
        public SchedulerStatisticsGroup SchedulerStatistics { get; }
    }

    [DebuggerDisplay("WorkItemGroup Name={Name} State={state}")]
    internal sealed class WorkItemGroup : OrleansSynchronizationContext, IThreadPoolWorkItem, IWorkItemScheduler
    {
        private enum WorkGroupStatus : byte
        {
            Waiting = 0,
            Runnable = 1,
            Running = 2
        }

        public enum WorkItemKind : byte
        {
            None,
            Task,
            SendOrPostCallback,
            Action,
            ActionWithState,
        }

        [StructLayout(LayoutKind.Auto)]
        public struct WorkItemEntry
        {
            public object WorkItem { get; set; }
            public object State { get; set; } // used for SendOrPostCallback from SynchronizationContext
            public WorkItemKind Type { get; set; }
            public OrleansSynchronizationContext Context { get; internal set; }

            public override string ToString() => Type switch
            {
                WorkItemKind.Task => OrleansTaskExtentions.ToString((Task)WorkItem),
                _ => $"WorkItem: {WorkItem?.ToString()} State: {State?.ToString()}"
            };
        }

        public override void Post(SendOrPostCallback callback, object state)
        {
            var workItem = new WorkItemEntry { State = state, Type = WorkItemKind.SendOrPostCallback, WorkItem = callback, Context = this };
            this.EnqueueWorkItem(workItem);
        }

        public override void Send(SendOrPostCallback callback, object state)
        {
            throw new NotSupportedException("Synchronous execution is not allowed");
        }

        private WorkGroupStatus state;
        private object lockable => workItems;
        private readonly Queue<WorkItemEntry> workItems;

        private long totalItemsEnqueued;
        private long totalItemsProcessed;
        private CoarseStopwatch lastLongQueueWarningTime;

        private WorkItemEntry currentTask;
        private long currentTaskStarted;
        private ActivationTaskScheduler _taskScheduler;

        internal WorkItemGroupShared Shared { get; }

        internal ActivationTaskScheduler TaskScheduler
        {
            get
            {
                if (_taskScheduler is not null) return _taskScheduler;
                lock (lockable)
                {
                    if (_taskScheduler is not null) return _taskScheduler;
                    return _taskScheduler = new ActivationTaskScheduler(this);
                }
            }
        }

        public override IGrainContext GrainContext { get; }

        internal bool IsSystemGroup => this.GrainContext is ISystemTargetBase;

        public override object CurrentRequest { get => default; set => throw new NotSupportedException(); }

        public string Name => GrainContext?.ToString() ?? "Unknown";

        internal int ExternalWorkItemCount
        {
            get { lock (lockable) { return WorkItemCount; } }
        }

        private WorkItemEntry CurrentTask
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
            WorkItemGroupShared shared)
        {
            Shared = shared;
            GrainContext = grainContext;
            state = WorkGroupStatus.Waiting;
            workItems = new Queue<WorkItemEntry>();
            totalItemsEnqueued = 0;
            totalItemsProcessed = 0;
            lastLongQueueWarningTime = CoarseStopwatch.StartNew();
        }

        public override void Schedule(SendOrPostCallback callback, object state, OrleansSynchronizationContext context)
        {
            var workItem = new WorkItemEntry{
                State = state,
                Type = WorkItemKind.SendOrPostCallback,
                WorkItem = callback,
                Context = context,
            };
            EnqueueWorkItem(workItem);
        }

        /// <summary>
        /// Adds a task to this activation.
        /// If we're adding it to the run list and we used to be waiting, now we're runnable.
        /// </summary>
        /// <param name="task">The work item to add.</param>
        public void EnqueueTask(Task task)
        {
            var workItem = new WorkItemEntry { WorkItem = task, Type = WorkItemKind.Task };
            EnqueueWorkItem(workItem);
        }

        public void Enqueue(Action task, bool forceAsync = false)
        {
            var workItem = new WorkItemEntry
            {
                Type = WorkItemKind.Action,
                WorkItem = task,
            };
            EnqueueWorkItem(workItem, forceAsync);
        }

        public void Enqueue(Action<object> task, object state, bool forceAsync = false)
        {
            var workItem = new WorkItemEntry
            {
                Type = WorkItemKind.ActionWithState,
                WorkItem = task,
                State = state,
            };
            EnqueueWorkItem(workItem, forceAsync);
        }

        public void EnqueueWorkItem(WorkItemEntry workItem, bool forceAsync = false)
        {
#if DEBUG
            if (Shared.Logger.IsEnabled(LogLevel.Trace))
            {
                Shared.Logger.LogTrace(
                    "EnqueueWorkItem {Task} into {GrainContext} when TaskScheduler.Current={TaskScheduler}",
                    workItem.ToString(),
                    this.GrainContext,
                    System.Threading.Tasks.TaskScheduler.Current);
            }
#endif

            lock (lockable)
            {
                long thisSequenceNumber = totalItemsEnqueued++;
                int count = WorkItemCount;

                workItems.Enqueue(workItem);
                int maxPendingItemsLimit = Shared.SchedulingOptions.MaxPendingWorkItemsSoftLimit;
                if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
                {
                    if (this.lastLongQueueWarningTime.ElapsedMilliseconds > 10_000)
                    {
                        LogLongQueueLengthWarning(count, maxPendingItemsLimit);
                        lastLongQueueWarningTime.Restart();
                    }
                }

                if (state != WorkGroupStatus.Waiting)
                {
                    return;
                }

                state = WorkGroupStatus.Runnable;
                ScheduleExecution(this);
            }
        }

        /// <summary>
        /// For debugger purposes only.
        /// </summary>
        internal IEnumerable<Task> GetScheduledTasks()
        {
            foreach (var workItem in workItems)
            {
                if (workItem.Type == WorkItemKind.Task)
                {
                    yield return (Task)workItem.WorkItem;
                }
            }
        }

        private static string DumpAsyncState(object o)
        {
            if (o is Delegate action)
                return action.Target is null ? action.Method.DeclaringType + "." + action.Method.Name
                    : action.Method.DeclaringType.Name + "." + action.Method.Name + ": " + DumpAsyncState(action.Target);

            if (o?.GetType() is { Name: "ContinuationWrapper" } wrapper
                && (wrapper.GetField("_continuation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? wrapper.GetField("m_continuation", BindingFlags.Instance | BindingFlags.NonPublic)
                    )?.GetValue(o) is Action continuation)
                return DumpAsyncState(continuation);

            return o?.ToString();
        }

        // Execute one or more turns for this activation. 
        // This method is always called in a single-threaded environment -- that is, no more than one
        // thread will be in this method at once -- but other asynch threads may still be queueing tasks, etc.
        public void Execute()
        {
            var outer = SynchronizationContext.Current;
            try
            {
                // Process multiple items -- drain the applicationMessageQueue (up to max items) for this physical activation
                var executionStopwatch = CoarseStopwatch.StartNew();
                var activationSchedulingQuantumMillis = Shared.ActivationSchedulingQuantumMillis;
                do
                {
                    lock (lockable)
                    {
                        state = WorkGroupStatus.Running;
                    }

                    // Get the first Work Item on the list
                    WorkItemEntry task;
                    lock (lockable)
                    {
                        if (workItems.Count > 0)
                        {
                            CurrentTask = task = workItems.Dequeue();
                        }
                        else // If the list is empty, then we're done
                        {
                            break;
                        }
                    }

#if DEBUG
                    if (Shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        Shared.Logger.LogTrace(
                        "About to execute task {Task} in GrainContext={GrainContext}",
                        task.ToString(),
                        this.GrainContext);
                    }
#endif
                    var taskStartMillis = executionStopwatch.ElapsedMilliseconds;
                    SynchronizationContext.SetSynchronizationContext(task.Context ?? this);

                    try
                    {
                        switch (task.Type)
                        {
                            case WorkItemKind.ActionWithState:
                                Unsafe.As<Action<object>>(task.WorkItem)(task.State);
                                break;
                            case WorkItemKind.Task:
                                TaskScheduler.RunTask(Unsafe.As<Task>(task.WorkItem));
                                break;
                            case WorkItemKind.SendOrPostCallback:
                                Unsafe.As<SendOrPostCallback>(task.WorkItem)(task.State);
                                break;
                            case WorkItemKind.Action:
                                Unsafe.As<Action>(task.WorkItem)();
                                break;
                            default:
                                ThrowUnknownWorkItemType(task);
                                return;
                        }
                    }
                    catch (Exception ex) when (LogInnerExecuteError(task, ex))
                    {
                        throw;
                    }
                    finally
                    {
                        totalItemsProcessed++;
                        var taskLengthMillis = executionStopwatch.ElapsedMilliseconds - taskStartMillis;
                        if (taskLengthMillis > Shared.TurnWarningLengthThresholdMilliseconds)
                        {
                            LogLongRunningTaskWarning(task, taskLengthMillis);
                        }

                        CurrentTask = default;
                    }
                }
                while (activationSchedulingQuantumMillis <= 0 || executionStopwatch.ElapsedMilliseconds < activationSchedulingQuantumMillis);
            }
            catch (Exception ex)
            {
                LogExecutionError(ex);
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

                SynchronizationContext.SetSynchronizationContext(outer);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnknownWorkItemType(in WorkItemEntry task) => throw new InvalidOperationException($"Unknown WorkItem: {task}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool LogInnerExecuteError(in WorkItemEntry task, Exception ex)
        {
            Shared.Logger.LogError(
                (int)ErrorCode.SchedulerExceptionFromExecute,
                ex,
                "Worker thread caught an exception thrown from execute by task {Task}. Exception: {Exception}",
                task.ToString(),
                ex);
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogExecutionError(Exception ex)
        {
            Shared.Logger.LogError(
                (int)ErrorCode.Runtime_Error_100032,
                ex,
                "Worker thread {Thread} caught an exception thrown from execute: {Exception}",
                Thread.CurrentThread.ManagedThreadId,
                ex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogLongRunningTaskWarning(in WorkItemEntry task, long taskLengthMillis)
        {
            var taskLength = TimeSpan.FromMilliseconds(taskLengthMillis);
            Shared.SchedulerStatistics.NumLongRunningTurns.Increment();
            Shared.Logger.LogWarning(
                (int)ErrorCode.SchedulerTurnTooLong3,
                "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}",
                task.ToString(),
                this.GrainContext.ToString(),
                taskLength.ToString("g"),
                Shared.SchedulingOptions.TurnWarningLengthThreshold,
                Thread.CurrentThread.ManagedThreadId.ToString());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogLongQueueLengthWarning(int count, int maxPendingItemsLimit)
        {
            Shared.Logger.LogWarning(
                (int)ErrorCode.SchedulerTooManyPendingItems,
                "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}",
                count,
                this.Name,
                maxPendingItemsLimit);
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
                if (CurrentTask is { Type: not WorkItemKind.None } task)
                {
                    sb.AppendFormat(
                        " Executing Task {0} for {1}",
                        task.ToString(),
                        TimeSpan.FromMilliseconds(Environment.TickCount64 - currentTaskStarted));
                }

                sb.AppendFormat("TaskRunner={0}; ", TaskScheduler);
                if (GrainContext is not null)
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

        public void QueueAction(Action action) => Enqueue(action);
        public void QueueAction(Action<object> action, object state) => Enqueue(action, state);
    }
}
