using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes one attempt of a durable task within a grain activation.
/// </summary>
/// <remarks>
/// The attempt's host execution-abort token is canceled when the activation shuts down or when the task is aborted:
/// <see cref="DurableExecutionContext.CancellationToken"/> observes it, while
/// <see cref="DurableExecutionContext.IsCancellationRequested"/> reports durable cancellation only. The runtime drains the
/// aborted attempt and discards its response; after shutdown, the next activation replays the task in a new context.
/// </remarks>
internal sealed class GrainDurableExecutionContext : DurableExecutionContext
{
    private readonly IDurableTaskGrainRuntime _runtime;
    private readonly TaskScheduler _scheduler;
    private long _utcNowTicks;

    // The attempt's abort source. It is linked to activation shutdown through a registration which the attempt releases
    // when it ends, so a completed attempt retains nothing on the shutdown token.
    private readonly CancellationTokenSource _abortSource;
    private readonly CancellationTokenRegistration _shutdownRegistration;
    private HashSet<string>? _childNames;
    private bool _deleted;
    private bool _aborted;
#if NET9_0_OR_GREATER
    private readonly Lock _idLock = new();
#else
    private readonly object _idLock = new();
#endif

    public GrainDurableExecutionContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        TaskScheduler scheduler,
        CancellationToken shutdownToken,
        DateTimeOffset? utcNow = null)
        : this(taskId, runtime, scheduler, shutdownToken, new CancellationTokenSource(), utcNow)
    {
    }

    private GrainDurableExecutionContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        TaskScheduler scheduler,
        CancellationToken shutdownToken,
        CancellationTokenSource abortSource,
        DateTimeOffset? utcNow)
        : base(taskId, abortSource.Token)
    {
        _runtime = runtime;
        _scheduler = scheduler;
        _utcNowTicks = (utcNow ?? runtime.UtcNow).UtcTicks;
        _abortSource = abortSource;
        _shutdownRegistration = shutdownToken.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(),
            abortSource);
    }

    /// <summary>
    /// Gets this execution's logical time: when the task first ran, advanced to the recorded completion time of each
    /// child whose terminal response the execution observes.
    /// </summary>
    /// <remarks>
    /// Child results keep their completion times, so a replay which observes the same children in the same order reads
    /// the same times. A delay therefore starts when the work before it completed.
    /// </remarks>
    public override DateTimeOffset UtcNow => new(Volatile.Read(ref _utcNowTicks), TimeSpan.Zero);

    private void ObserveCompletion(TaskId childId)
    {
        if (_runtime.GetCompletedAt(childId) is { } completedAt)
        {
            ObserveTime(completedAt);
        }
    }

    private void ObserveTime(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        for (var current = Volatile.Read(ref _utcNowTicks); ticks > current;)
        {
            var observed = Interlocked.CompareExchange(ref _utcNowTicks, ticks, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// Marks this attempt's task as deleted with its execution tree, so the attempt can no longer schedule durable work.
    /// </summary>
    /// <remarks>The runtime deletes tasks on the activation scheduler, where this attempt schedules its work.</remarks>
    internal void MarkDeleted() => _deleted = true;

    /// <summary>
    /// Marks this attempt's task as aborted, so the attempt can no longer schedule durable work. The runtime discards the
    /// attempt's result and calls <see cref="SignalAbort"/> once the abort is durable.
    /// </summary>
    internal void MarkAborted() => _aborted = true;

    /// <summary>Cancels the attempt's host execution-abort token, so the attempt stops.</summary>
    internal void SignalAbort()
    {
        Debug.Assert(_aborted, "Only an aborted attempt is signaled.");
        _abortSource.Cancel();
    }

    /// <summary>Releases the attempt's link to activation shutdown once the attempt has ended.</summary>
    internal void OnAttemptCompleted() => _shutdownRegistration.Dispose();

    private void ThrowIfDeleted()
    {
        if (_aborted)
        {
            throw new OperationCanceledException($"Durable task '{TaskId}' was aborted.", _abortSource.Token);
        }

        if (_deleted)
        {
            throw new OperationCanceledException($"Durable task '{TaskId}' was deleted with its execution tree.");
        }
    }

    protected override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(
        TaskId taskId,
        DurableTask task,
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => ScheduleChildTaskCoreAsync(taskId, task, cancellationToken));

    private async ValueTask<IScheduledTaskHandle> ScheduleChildTaskCoreAsync(
        TaskId taskId,
        DurableTask task,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var handle = await _runtime.ScheduleChildAsync(taskId, task, executionCts.Token);
        return new ExecutionTaskHandle(handle, this);
    }

    protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => ScheduleDelayCoreAsync(taskId, dueTime, cancellationToken));

    private async ValueTask<DurableTaskResponse> ScheduleDelayCoreAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        if (taskId != TaskId)
        {
            ThrowIfNotChildTaskId(taskId);
        }

        ThrowIfDeleted();

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await _runtime.ScheduleDelayAsync(taskId, dueTime, executionCts.Token);
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
        => RunOnScheduler(
            _scheduler,
            () => GetChildTaskHandleCore(taskId));

    /// <remarks>
    /// Only durable grain calls can be detached: the target recovers a root task from its serialized request. Durable
    /// cancellation of this execution doesn't abandon the start; host abort does.
    /// </remarks>
    protected override ValueTask DetachTaskAsync(
        TaskId markerId,
        TaskId rootId,
        DurableTask taskDefinition,
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => DetachTaskCoreAsync(markerId, rootId, taskDefinition, cancellationToken));

    private async ValueTask DetachTaskCoreAsync(
        TaskId markerId,
        TaskId rootId,
        DurableTask taskDefinition,
        CancellationToken cancellationToken)
    {
        ThrowIfNotChildTaskId(markerId);
        ThrowIfDeleted();
        if (taskDefinition is not IDurableTaskRequest request)
        {
            throw new NotSupportedException("Only durable grain calls can be detached.");
        }

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        await _runtime.DetachAsync(rootId, request, markerId, executionCts.Token);
    }

    private IScheduledTaskHandle GetChildTaskHandleCore(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return new ExecutionTaskHandle(_runtime.GetScheduledTaskHandle(taskId), this);
    }

    /// <remarks>
    /// The wait observes the signals of the invoked task whose execution tree contains this execution, and advances this
    /// execution's logical time to when the signal was recorded.
    /// </remarks>
    protected override ValueTask<DurableTaskResponse> WaitForSignalAsync(string name, CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => WaitForSignalCoreAsync(name, cancellationToken));

    private async ValueTask<DurableTaskResponse> WaitForSignalCoreAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var (value, recordedAt) = await _runtime.WaitForSignalAsync(TaskId, name, executionCts.Token);
        ObserveTime(recordedAt);
        return value;
    }

    /// <summary>
    /// Sends a signal to the invoked task <paramref name="taskId"/> on <paramref name="target"/>, recording the send under a
    /// generated child identifier of this execution, so a replay doesn't send it again.
    /// </summary>
    /// <remarks>
    /// The identifier is generated when the send begins, so sends begin in deterministic order, like unnamed children.
    /// Durable cancellation of this execution doesn't abandon the send; host abort does.
    /// </remarks>
    internal ValueTask SendSignalAsync(
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken)
    {
        var markerId = CreateChildTaskId(name: null);
        return RunOnScheduler(
            _scheduler,
            () => SendSignalCoreAsync(markerId, target, taskId, name, value, cancellationToken));
    }

    private async ValueTask SendSignalCoreAsync(
        TaskId markerId,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken)
    {
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        await _runtime.SendSignalAsync(markerId, target, taskId, name, value, executionCts.Token);
    }

    protected override ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => SelectCompletionCoreAsync(decisionId, candidates, cancellationToken));

    private async ValueTask<TaskId> SelectCompletionCoreAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfNotChildTaskId(decisionId);
        foreach (var candidate in candidates)
        {
            ThrowIfNotChildTaskId(candidate);
        }

        ThrowIfDeleted();

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await _runtime.SelectCompletionAsync(decisionId, candidates, executionCts.Token);
    }

    protected override TaskId CreateChildTaskId(string? name)
    {
        lock (_idLock)
        {
            var baseId = base.CreateChildTaskId(name);
            if (name is null)
            {
                return baseId;
            }

            if (_childNames is null)
            {
                _childNames = new([name], StringComparer.Ordinal);
            }
            else if (!_childNames.Add(name))
            {
                return base.CreateChildTaskId(null);
            }

            return baseId;
        }
    }

    private void ThrowIfNotChildTaskId(TaskId taskId)
    {
        if (taskId == TaskId || !TaskId.IsAncestorOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a descendant of this task '{TaskId}'.");
        }
    }

    private CancellationTokenSource CreateExecutionCancellationSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _abortSource.Token);

    private static ValueTask RunOnScheduler(TaskScheduler scheduler, Func<ValueTask> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state => await ((Func<ValueTask>)state!)(),
                callback,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler).Unwrap());
    }

    private static ValueTask<TResult> RunOnScheduler<TResult>(
        TaskScheduler scheduler,
        Func<ValueTask<TResult>> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state => await ((Func<ValueTask<TResult>>)state!)(),
                callback,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler).Unwrap());
    }

    private static TResult RunOnScheduler<TResult>(
        TaskScheduler scheduler,
        Func<TResult> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return Task.Factory.StartNew(
            static state => ((Func<TResult>)state!)(),
            callback,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).GetAwaiter().GetResult();
    }

    private sealed class ExecutionTaskHandle(
        IScheduledTaskHandle inner,
        GrainDurableExecutionContext owner) : IScheduledTaskHandle
    {
        public TaskId TaskId => inner.TaskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => CancelCoreAsync(cancellationToken));

        private async ValueTask CancelCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            await inner.CancelAsync(executionCts.Token);
        }

        public ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => AbortCoreAsync(reason, cancellationToken));

        private async ValueTask AbortCoreAsync(string reason, CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            await inner.AbortAsync(reason, executionCts.Token);
        }

        public ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => PollCoreAsync(options, cancellationToken));

        private async ValueTask<DurableTaskResponse> PollCoreAsync(
            PollingOptions options,
            CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            return Observe(await inner.PollAsync(options, executionCts.Token));
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => WaitCoreAsync(cancellationToken));

        private async ValueTask<DurableTaskResponse> WaitCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            return Observe(await inner.WaitAsync(executionCts.Token));
        }

        private DurableTaskResponse Observe(DurableTaskResponse response)
        {
            if (response.IsCompleted)
            {
                owner.ObserveCompletion(inner.TaskId);
            }

            return response;
        }
    }
}