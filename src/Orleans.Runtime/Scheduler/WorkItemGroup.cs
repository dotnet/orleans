#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Scheduler;

[DebuggerDisplay("WorkItemGroup Context={GrainContext} State={state}")]
internal sealed class WorkItemGroup : IThreadPoolWorkItem, IWorkItemScheduler
{
    private enum WorkGroupStatus : byte
    {
        Waiting = 0,
        Runnable = 1,
        Running = 2
    }

    private readonly ILogger _log;
    private readonly object _lockObj = new();
    private readonly Queue<Task> _workItems = new();
    private readonly SchedulingOptions _schedulingOptions;

    private long _totalItemsEnqueued;
    private long _totalItemsProcessed;
    private long _lastLongQueueWarningTimestamp;

    private WorkGroupStatus _state;
    private Task? _currentTask;
    private long _currentTaskStarted;

    internal ActivationTaskScheduler TaskScheduler { get; }

    public IGrainContext GrainContext { get; set; }

    internal int ExternalWorkItemCount
    {
        get { lock (_lockObj) { return _workItems.Count; } }
    }

    public WorkItemGroup(
        IGrainContext grainContext,
        ILogger<WorkItemGroup> logger,
        ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger,
        IOptions<SchedulingOptions> schedulingOptions)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        GrainContext = grainContext;
        _schedulingOptions = schedulingOptions.Value;
        _state = WorkGroupStatus.Waiting;
        _log = logger;
        TaskScheduler = new ActivationTaskScheduler(this, activationTaskSchedulerLogger);
    }

    /// <summary>
    /// Adds a task to this activation.
    /// If we're adding it to the run list and we used to be waiting, now we're runnable.
    /// </summary>
    /// <param name="task">The work item to add.</param>
    public void EnqueueTask(Task task)
    {
#if DEBUG
        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace(
                "EnqueueWorkItem {Task} into {GrainContext} when TaskScheduler.Current={TaskScheduler}",
                task,
                GrainContext,
                System.Threading.Tasks.TaskScheduler.Current);
        }
#endif

        lock (_lockObj)
        {
            long thisSequenceNumber = _totalItemsEnqueued++;
            int count = _workItems.Count;

            _workItems.Enqueue(task);
            int maxPendingItemsLimit = _schedulingOptions.MaxPendingWorkItemsSoftLimit;
            if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
            {
                var now = Environment.TickCount64;
                if (now > _lastLongQueueWarningTimestamp + 10_000)
                {
                    LogTooManyTasksInQueue(count, maxPendingItemsLimit);
                }

                _lastLongQueueWarningTimestamp = now;
            }

            if (_state != WorkGroupStatus.Waiting)
            {
                return;
            }

            _state = WorkGroupStatus.Runnable;
#if DEBUG
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace(
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
        _log.LogWarning(
            (int)ErrorCode.SchedulerTooManyPendingItems,
            "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}",
            count,
            GrainContext?.ToString() ?? "Unknown",
            maxPendingItemsLimit);
    }

    /// <summary>
    /// For debugger purposes only.
    /// </summary>
    internal IEnumerable<Task> GetScheduledTasks()
    {
        foreach (var task in _workItems)
        {
            yield return task;
        }
    }

    // Execute one or more turns for this activation.
    // This method is always called in a single-threaded environment -- that is, no more than one
    // thread will be in this method at once -- but other async threads may still be queueing tasks, etc.
    public void Execute()
    {
        RuntimeContext.SetExecutionContext(GrainContext, out var originalContext);
        var turnWarningDurationMs = (long)Math.Ceiling(_schedulingOptions.TurnWarningLengthThreshold.TotalMilliseconds);
        var activationSchedulingQuantumMs = (long)_schedulingOptions.ActivationSchedulingQuantum.TotalMilliseconds;
        try
        {

            // Process multiple items -- drain the queue (up to max items) for this activation
            long loopStart, taskStart, taskEnd;
            loopStart = taskStart = taskEnd = Environment.TickCount64;
            do
            {
                Task task;
                lock (_lockObj)
                {
                    _state = WorkGroupStatus.Running;

                    // Get the first Work Item on the list
                    if (_workItems.Count > 0)
                    {
                        _currentTask = task = _workItems.Dequeue();
                        _currentTaskStarted = taskStart;
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
                finally
                {
                    _totalItemsProcessed++;
                    taskEnd = Environment.TickCount64;
                    var taskDurationMs = taskEnd - taskStart;
                    taskStart = taskEnd;
                    if (taskDurationMs > turnWarningDurationMs)
                    {
                        SchedulerInstruments.LongRunningTurnsCounter.Add(1);
                        LogLongRunningTurn(task, taskDurationMs);
                    }

                    _currentTask = null;
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
            lock (_lockObj)
            {
                if (_workItems.Count > 0)
                {
                    _state = WorkGroupStatus.Runnable;
                    ScheduleExecution(this);
                }
                else
                {
                    _state = WorkGroupStatus.Waiting;
                }
            }

            RuntimeContext.ResetExecutionContext(originalContext);
        }
    }

#if DEBUG
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogTaskStart(Task task)
    {
        if (_log.IsEnabled(LogLevel.Trace))
        {
            _log.LogTrace(
            "About to execute task '{Task}' in GrainContext={GrainContext}",
            task,
            GrainContext);
        }
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogTaskLoopError(Exception ex)
    {
        _log.LogError(
            (int)ErrorCode.Runtime_Error_100032,
            ex,
            "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute",
            Thread.CurrentThread.ManagedThreadId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogLongRunningTurn(Task task, long taskDurationMs)
    {
        if (Debugger.IsAttached)
        {
            return;
        }

        var taskDuration = TimeSpan.FromMilliseconds(taskDurationMs);
        _log.LogWarning(
            (int)ErrorCode.SchedulerTurnTooLong3,
            "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}",
            task.AsyncState ?? task,
            GrainContext.ToString(),
            taskDuration.ToString("g"),
            _schedulingOptions.TurnWarningLengthThreshold,
            Thread.CurrentThread.ManagedThreadId.ToString());
    }

    public override string ToString() => $"{(GrainContext is SystemTarget ? "System*" : "")}WorkItemGroup:Name={GrainContext?.ToString() ?? "Unknown"},WorkGroupStatus={_state}";

    public string DumpStatus()
    {
        lock (_lockObj)
        {
            var sb = new StringBuilder();
            sb.Append(this);
            sb.AppendFormat(". Currently QueuedWorkItems={0}; Total Enqueued={1}; Total processed={2}; ",
                _workItems.Count, _totalItemsEnqueued, _totalItemsProcessed);
            if (_currentTask is Task task)
            {
                sb.AppendFormat(" Executing Task Id={0} Status={1} for {2}.",
                    task.Id, task.Status, TimeSpan.FromMilliseconds(Environment.TickCount64 - _currentTaskStarted));
            }

            sb.AppendFormat("TaskRunner={0}; ", TaskScheduler);
            if (GrainContext != null)
            {
                var detailedStatus = GrainContext switch
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
