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
    internal sealed class WorkItemGroup : IThreadPoolWorkItem, IWorkItemScheduler
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
        private long _lastLongQueueWarningTimestamp;

        private Task currentTask;
        private long currentTaskStarted;

        private readonly SchedulingOptions schedulingOptions;

        internal ActivationTaskScheduler TaskScheduler { get; }

        public IGrainContext GrainContext { get; set; }

        internal bool IsSystemGroup => this.GrainContext is ISystemTargetBase;

        public string Name => GrainContext?.ToString() ?? "Unknown";

        internal int ExternalWorkItemCount
        {
            get { lock (lockable) { return workItems.Count; } }
        }

        private Task CurrentTask
        {
            get => currentTask;
            set => currentTask = value;
        }

        public WorkItemGroup(
            IGrainContext grainContext,
            ILogger<WorkItemGroup> logger,
            ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
            IOptions<SchedulingOptions> schedulingOptions)
        {
            GrainContext = grainContext;
            this.schedulingOptions = schedulingOptions.Value;
            state = WorkGroupStatus.Waiting;
            workItems = new Queue<Task>();
            lockable = new object();
            totalItemsEnqueued = 0;
            totalItemsProcessed = 0;
            TaskScheduler = new ActivationTaskScheduler(this, activationTaskSchedulerLogger);
            log = logger;
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
                int count = workItems.Count;

                workItems.Enqueue(task);
                int maxPendingItemsLimit = schedulingOptions.MaxPendingWorkItemsSoftLimit;
                if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
                {
                    var now = Environment.TickCount64;
                    if (now > _lastLongQueueWarningTimestamp + 10_000)
                    {
                        LogTooManyTasksInQueue(count, maxPendingItemsLimit);
                    }

                    _lastLongQueueWarningTimestamp = now;
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogTooManyTasksInQueue(int count, int maxPendingItemsLimit)
        {
            log.LogWarning(
                (int)ErrorCode.SchedulerTooManyPendingItems,
                "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}",
                count,
                this.Name,
                maxPendingItemsLimit);
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
        // thread will be in this method at once -- but other async threads may still be queueing tasks, etc.
        public void Execute()
        {
            RuntimeContext.SetExecutionContext(GrainContext, out var originalContext);
            var turnWarningDurationMs = (long)Math.Ceiling(schedulingOptions.TurnWarningLengthThreshold.TotalMilliseconds);
            var activationSchedulingQuantumMs = (long)schedulingOptions.ActivationSchedulingQuantum.TotalMilliseconds;
            try
            {

                // Process multiple items -- drain the queue (up to max items) for this activation
                var loopStart = Environment.TickCount64;
                var taskStart = loopStart;
                var taskEnd = taskStart;
                do
                {
                    Task task;
                    lock (lockable)
                    {
                        state = WorkGroupStatus.Running;

                        // Get the first Work Item on the list
                        if (workItems.Count > 0)
                        {
                            CurrentTask = task = workItems.Dequeue();
                            currentTaskStarted = taskStart;
                        }
                        else
                        {
                            // If the list is empty, then we're done
                            break;
                        }
                    }

#if DEBUG
                    LogTaskStart(task);
#endif
                    try
                    {
                        TaskScheduler.RunTaskFromWorkItemGroup(task);
                    }
                    catch (Exception ex)
                    {
                        LogTaskRunError(task, ex);
                        throw;
                    }
                    finally
                    {
                        totalItemsProcessed++;
                        taskEnd = Environment.TickCount64;
                        var taskDurationMs = taskEnd - taskStart;
                        taskStart = taskEnd;
                        if (taskDurationMs > turnWarningDurationMs)
                        {
                            SchedulerInstruments.LongRunningTurnsCounter.Add(1);
                            LogLongRunningTurn(task, taskDurationMs);
                        }

                        CurrentTask = null;
                    }
                }
                while (activationSchedulingQuantumMs <= 0 || taskEnd - loopStart < activationSchedulingQuantumMs);
            }
            catch (Exception ex)
            {
                LogTaskLoopError(ex);
            }
            finally
            {
                // Now we're not Running anymore.
                // If we left work items on our run list, we're Runnable, and need to go back on the silo run queue;
                // If our run list is empty, then we're waiting.
                lock (lockable)
                {
                    if (workItems.Count > 0)
                    {
                        state = WorkGroupStatus.Runnable;
                        ScheduleExecution(this);
                    }
                    else
                    {
                        state = WorkGroupStatus.Waiting;
                    }
                }

                RuntimeContext.ResetExecutionContext(originalContext);
            }
        }

#if DEBUG
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogTaskStart(Task task)
        {
            if (log.IsEnabled(LogLevel.Trace))
            {
                log.LogTrace(
                "About to execute task {Task} in GrainContext={GrainContext}",
                OrleansTaskExtentions.ToString(task),
                this.GrainContext);
            }
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogTaskLoopError(Exception ex)
        {
            this.log.LogError(
                (int)ErrorCode.Runtime_Error_100032,
                ex,
                "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute",
                Thread.CurrentThread.ManagedThreadId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogTaskRunError(Task task, Exception ex)
        {
            this.log.LogError(
                (int)ErrorCode.SchedulerExceptionFromExecute,
                ex,
                "Worker thread caught an exception thrown from Execute by task {Task}",
                OrleansTaskExtentions.ToString(task));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogLongRunningTurn(Task task, long taskDurationMs)
        {
            var taskDuration = TimeSpan.FromMilliseconds(taskDurationMs);
            this.log.LogWarning(
                (int)ErrorCode.SchedulerTurnTooLong3,
                "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}",
                OrleansTaskExtentions.ToString(task),
                this.GrainContext.ToString(),
                taskDuration.ToString("g"),
                schedulingOptions.TurnWarningLengthThreshold,
                Thread.CurrentThread.ManagedThreadId.ToString());
        }

        public override string ToString() => $"{(IsSystemGroup ? "System*" : "")}WorkItemGroup:Name={Name},WorkGroupStatus={state}";

        public string DumpStatus()
        {
            lock (lockable)
            {
                var sb = new StringBuilder();
                sb.Append(this);
                sb.AppendFormat(". Currently QueuedWorkItems={0}; Total Enqueued={1}; Total processed={2}; ",
                    workItems.Count, totalItemsEnqueued, totalItemsProcessed);
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
        public static void ScheduleExecution(WorkItemGroup workItem) => ThreadPool.UnsafeQueueUserWorkItem(workItem, preferLocal: true);

        public void QueueAction(Action action) => TaskScheduler.QueueAction(action);
        public void QueueAction(Action<object> action, object state) => TaskScheduler.QueueAction(action, state);
        public void QueueTask(Task task) => task.Start(TaskScheduler);
    }
}
