using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes one attempt of a durable task within a grain activation.
/// </summary>
/// <remarks>
/// The activation shutdown token is the attempt's host execution-abort token: <see cref="DurableExecutionContext.CancellationToken"/>
/// observes it, while <see cref="DurableExecutionContext.IsCancellationRequested"/> reports durable cancellation only.
/// Shutdown drains the aborted attempt, discards its response, and the next activation replays the task in a new context.
/// </remarks>
internal sealed class GrainDurableExecutionContext(
    TaskId taskId,
    IDurableTaskGrainRuntime runtime,
    TaskScheduler scheduler,
    CancellationToken shutdownToken,
    DateTimeOffset? utcNow = null) : DurableExecutionContext(taskId, shutdownToken)
{
    private readonly IDurableTaskGrainRuntime _runtime = runtime;
    private readonly TaskScheduler _scheduler = scheduler;
    private long _utcNowTicks = (utcNow ?? runtime.UtcNow).UtcTicks;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private HashSet<string>? _childNames;
    private bool _deleted;
#if NET9_0_OR_GREATER
    private readonly Lock _idLock = new();
#else
    private readonly object _idLock = new();
#endif

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
        if (_runtime.GetCompletedAt(childId) is not { } completedAt)
        {
            return;
        }

        var ticks = completedAt.UtcTicks;
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

    private void ThrowIfDeleted()
    {
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

    private IScheduledTaskHandle GetChildTaskHandleCore(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return new ExecutionTaskHandle(_runtime.GetScheduledTaskHandle(taskId), this);
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
            _shutdownToken);

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
                owner._shutdownToken);
            await inner.CancelAsync(executionCts.Token);
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
                owner._shutdownToken);
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
                owner._shutdownToken);
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