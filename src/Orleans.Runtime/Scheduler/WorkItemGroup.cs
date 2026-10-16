
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Scheduler;

internal readonly struct WorkItem
{
    public enum WorkItemType : byte
    {
        Task = 0,
        SendOrPostCallback = 1,
        ActionOfObject = 2
    }

    public readonly object Callback;
    public readonly object? State;
    public readonly WorkItemType Type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorkItem(Task task)
    {
        Callback = task;
        State = null;
        Type = WorkItemType.Task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorkItem(SendOrPostCallback callback, object? state)
    {
        Callback = callback;
        State = state;
        Type = WorkItemType.SendOrPostCallback;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorkItem(Action<object?> callback, object? state)
    {
        Callback = callback;
        State = state;
        Type = WorkItemType.ActionOfObject;
    }
}

[DebuggerDisplay("WorkItemGroup Context={GrainContext} State={_state}")]
internal sealed partial class WorkItemGroup : SynchronizationContext, IThreadPoolWorkItem, IWorkItemScheduler
{
    private static readonly ContextCallback ExecuteCallback = static state => ((WorkItemGroup)state!).ExecuteCurrentWorkItem();

    private enum WorkGroupStatus : byte
    {
        Waiting = 0,
        Runnable = 1,
        Running = 2
    }

#if NET9_0_OR_GREATER
    private readonly Lock _lockObj = new();
#else
    private readonly object _lockObj = new();
#endif
    private readonly Queue<WorkItem> _workItems = new();
    private readonly SchedulingOptions _schedulingOptions;
    private readonly SchedulerInstruments _schedulerInstruments;

    private long _totalItemsEnqueued;
    private long _totalItemsProcessed;
    private long _lastLongQueueWarningTimestamp;

    private WorkGroupStatus _state;
    private Task? _currentTask;
    private WorkItem _currentWorkItem;
    private long _currentTaskStarted;

    // Dummy task used to make TaskScheduler.Current return our scheduler
    private readonly Task _schedulerTask;

    internal ActivationTaskScheduler TaskScheduler { get; }

    public IGrainContext GrainContext { get; set; }

    internal ILogger Logger => GrainContext switch
    {
        ActivationData activation => activation.Shared.SchedulerLogger,
        SystemTarget systemTarget => systemTarget.SchedulerLogger,
        _ => GrainContext.ActivationServices.GetRequiredService<ILogger<WorkItemGroup>>()
    };

    internal int ExternalWorkItemCount
    {
        get { lock (_lockObj) { return _workItems.Count; } }
    }

    public WorkItemGroup(
        IGrainContext grainContext,
        IOptions<SchedulingOptions> schedulingOptions,
        SchedulerInstruments schedulerInstruments)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        GrainContext = grainContext;
        _schedulingOptions = schedulingOptions.Value;
        _schedulerInstruments = schedulerInstruments;
        _state = WorkGroupStatus.Waiting;
        TaskScheduler = new ActivationTaskScheduler(this);

        _schedulerTask = CreateSchedulerTask(TaskScheduler);
    }

    private static Task CreateSchedulerTask(TaskScheduler scheduler)
    {
        Task task;
        if (ExecutionContext.IsFlowSuppressed())
        {
            task = new Task(static () => { }, TaskCreationOptions.DenyChildAttach);
        }
        else
        {
            using var suppressExecutionContext = ExecutionContext.SuppressFlow();
            task = new Task(static () => { }, TaskCreationOptions.DenyChildAttach);
        }

        // UnsafeAccessor is compatible with trimming and NativeAOT. Validate the runtime's Task layout and
        // TaskScheduler.Current semantics during initialization so unsupported runtime changes fail fast.
        try
        {
            GetTaskSchedulerRef(task) = scheduler;
            var previousTask = GetCurrentTask();
            try
            {
                SetCurrentTask(task);
                if (!ReferenceEquals(System.Threading.Tasks.TaskScheduler.Current, scheduler))
                {
                    throw new PlatformNotSupportedException(
                        "The current runtime does not support Orleans activation scheduler context.");
                }
            }
            finally
            {
                SetCurrentTask(previousTask);
            }
        }
        catch (Exception exception) when (exception is MissingFieldException or MissingMethodException)
        {
            throw new PlatformNotSupportedException(
                "The current runtime does not expose the Task internals required for Orleans activation scheduler context.",
                exception);
        }

        return task;
    }

    /// <summary>
    /// Adds a task to this activation.
    /// If we're adding it to the run list and we used to be waiting, now we're runnable.
    /// </summary>
    /// <param name="task">The work item to add.</param>
    public void EnqueueTask(Task task)
    {
#if DEBUG
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            LogTraceEnqueueWorkItem(Logger, task, GrainContext, System.Threading.Tasks.TaskScheduler.Current);
        }
#endif

        EnqueueWorkItem(new WorkItem(task));
    }

    private void EnqueueWorkItem(WorkItem workItem)
    {
        lock (_lockObj)
        {
            long thisSequenceNumber = _totalItemsEnqueued++;
            int count = _workItems.Count;

            _workItems.Enqueue(workItem);
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
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace(
                    "Add to RunQueue #{SequenceNumber}, onto {GrainContext}",
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
        LogWarningTooManyPendingItems(Logger, count, GrainContext?.ToString() ?? "Unknown", maxPendingItemsLimit);
    }

    /// <summary>
    /// For debugger purposes only.
    /// </summary>
    internal IEnumerable<Task> GetScheduledTasks()
    {
        lock (_lockObj)
        {
            var tasks = _workItems
                .Where(item => item.Type == WorkItem.WorkItemType.Task)
                .Select(item => Unsafe.As<Task>(item.Callback));
            return [.. tasks];
        }
    }

    // Execute one or more turns for this activation.
    // This method is always called in a single-threaded environment -- that is, no more than one
    // thread will be in this method at once -- but other async threads may still be queueing tasks, etc.
    public void Execute()
    {
        RuntimeContext.SetExecutionContext(GrainContext, out var originalContext);

        // Set t_currentTask so TaskScheduler.Current returns our ActivationTaskScheduler
        var previousTask = GetCurrentTask();
        SetCurrentTask(_schedulerTask);

        var turnWarningDurationMs = (long)Math.Ceiling(_schedulingOptions.TurnWarningLengthThreshold.TotalMilliseconds);
        var activationSchedulingQuantumMs = (long)_schedulingOptions.ActivationSchedulingQuantum.TotalMilliseconds;
        try
        {

            // Process multiple items -- drain the queue (up to max items) for this activation
            long loopStart, taskStart, taskEnd;
            loopStart = taskStart = taskEnd = Environment.TickCount64;
            do
            {
                WorkItem workItem;
                lock (_lockObj)
                {
                    _state = WorkGroupStatus.Running;

                    // Get the first Work Item on the list
                    if (_workItems.Count > 0)
                    {
                        workItem = _workItems.Dequeue();
                        _currentTaskStarted = taskStart;
                    }
                    else
                    {
                        // If the list is empty, then we're done
                        break;
                    }
                }

                try
                {
                    switch (workItem.Type)
                    {
                        case WorkItem.WorkItemType.Task:
                            {
                                var task = Unsafe.As<Task>(workItem.Callback);
                                _currentTask = task;
#if DEBUG
                                LogTaskStart(task);
#endif
                                TaskScheduler.RunTaskFromWorkItemGroup(task);
                            }
                            break;
                        case WorkItem.WorkItemType.SendOrPostCallback:
                        case WorkItem.WorkItemType.ActionOfObject:
                            _currentWorkItem = workItem;
                            ExecutionContext.Run(CaptureExecutionContext(), ExecuteCallback, this);
                            break;
                    }
                }
                finally
                {
                    _totalItemsProcessed++;
                    taskEnd = Environment.TickCount64;
                    var taskDurationMs = taskEnd - taskStart;
                    taskStart = taskEnd;
                    if (taskDurationMs > turnWarningDurationMs)
                    {
                        _schedulerInstruments.OnLongRunningTurn();
                        LogLongRunningTurn(workItem, taskDurationMs);
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
            SetCurrentTask(previousTask);
            RuntimeContext.ResetExecutionContext(originalContext);
        }
    }

#if DEBUG
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogTaskStart(Task task)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            LogTraceAboutToExecuteTask(Logger, task, GrainContext);
        }
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogTaskLoopError(Exception ex)
    {
        LogErrorTaskLoop(Logger, ex, Environment.CurrentManagedThreadId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogLongRunningTurn(WorkItem workItem, long taskDurationMs)
    {
        if (Debugger.IsAttached)
        {
            return;
        }

        var taskDuration = TimeSpan.FromMilliseconds(taskDurationMs);
        var description = workItem.Callback is Task task
            ? task.AsyncState ?? task
            : workItem.State ?? workItem.Callback;
        LogWarningLongRunningTurn(
            Logger,
            description,
            GrainContext?.ToString() ?? "Unknown",
            taskDuration.ToString("g"),
            _schedulingOptions.TurnWarningLengthThreshold,
            Environment.CurrentManagedThreadId.ToString());
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

    public void QueueAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnqueueWorkItem(new WorkItem((Action<object?>)(static state => ((Action)state!)()), action));
    }

    public void QueueAction(Action<object> action, object state)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnqueueWorkItem(new WorkItem(Unsafe.As<Action<object?>>(action), state));
    }

    internal void QueueNullableAction(Action<object?> action, object? state)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnqueueWorkItem(new WorkItem(action, state));
    }
    public void QueueTask(Task task) => task.Start(TaskScheduler);

    private void ExecuteCurrentWorkItem()
    {
        try
        {
            var workItem = _currentWorkItem;
            if (workItem.Type == WorkItem.WorkItemType.SendOrPostCallback)
            {
                Unsafe.As<SendOrPostCallback>(workItem.Callback)(workItem.State);
            }
            else
            {
                Unsafe.As<Action<object?>>(workItem.Callback)(workItem.State);
            }
        }

        catch (Exception exception)
        {
            LogTaskLoopError(exception);
        }
        finally
        {
            _currentWorkItem = default;
        }
    }

    private static ExecutionContext CaptureExecutionContext()
    {
        if (ExecutionContext.Capture() is { } executionContext)
        {
            return executionContext;
        }

        Debug.Assert(ExecutionContext.IsFlowSuppressed());
        ExecutionContext.RestoreFlow();
        try
        {
            return ExecutionContext.Capture()!;
        }
        finally
        {
            ExecutionContext.SuppressFlow();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "EnqueueWorkItem {Task} into {GrainContext} when TaskScheduler.Current={TaskScheduler}"
    )]
    private static partial void LogTraceEnqueueWorkItem(ILogger logger, Task task, IGrainContext grainContext, TaskScheduler taskScheduler);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Add to RunQueue {Task}, #{SequenceNumber}, onto {GrainContext}"
    )]
    private static partial void LogTraceAddToRunQueue(ILogger logger, Task task, long sequenceNumber, IGrainContext grainContext);

    [LoggerMessage(
        EventId = (int)ErrorCode.SchedulerTooManyPendingItems,
        Level = LogLevel.Warning,
        Message = "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}"
    )]
    private static partial void LogWarningTooManyPendingItems(ILogger logger, int pendingWorkItemCount, string workGroupName, int warningThreshold);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "About to execute task '{Task}' in GrainContext={GrainContext}"
    )]
    private static partial void LogTraceAboutToExecuteTask(ILogger logger, Task task, IGrainContext grainContext);

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100032,
        Level = LogLevel.Error,
        Message = "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute"
    )]
    private static partial void LogErrorTaskLoop(ILogger logger, Exception exception, int thread);

    [LoggerMessage(
        EventId = (int)ErrorCode.SchedulerTurnTooLong3,
        Level = LogLevel.Warning,
        Message = "Task {Task} in WorkGroup {GrainContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}"
    )]
    private static partial void LogWarningLongRunningTurn(ILogger logger, object task, string grainContext, string duration, TimeSpan turnWarningLengthThreshold, string thread);

    /// <summary>
    /// Asynchronously posts a callback to be executed on this WorkItemGroup.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        EnqueueWorkItem(new WorkItem(d, state));
    }

    /// <summary>
    /// Synchronously sends a callback. Not supported - throws.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <summary>
    /// Creates a copy (returns same instance for single-threaded behavior).
    /// </summary>
    public override SynchronizationContext CreateCopy() => this;

    // TaskScheduler.Current derives its value from Task.t_currentTask. These accessors preserve the
    // established activation scheduler context for allocation-free callbacks and are validated above.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "t_currentTask")]
    private static extern ref Task? GetCurrentTaskRef(Task? _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Task? GetCurrentTask() => GetCurrentTaskRef(null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCurrentTask(Task? task) => GetCurrentTaskRef(null) = task;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_taskScheduler")]
    private static extern ref TaskScheduler? GetTaskSchedulerRef(Task task);
}
