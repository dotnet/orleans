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
/// The activation shutdown token is the attempt's host execution-abort token: delegate and delay operations observe it,
/// while <see cref="DurableExecutionContext.CancellationToken"/> observes durable cancellation only. Shutdown drains the
/// aborted attempt, discards its response, and the next activation replays the task in a new context.
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
    private readonly DateTimeOffset _utcNow = utcNow ?? runtime.UtcNow;
    private readonly CancellationToken _shutdownToken = shutdownToken;
    private HashSet<string>? _childNames;
    private bool _deleted;
#if NET9_0_OR_GREATER
    private readonly Lock _idLock = new();
#else
    private readonly object _idLock = new();
#endif

    public override DateTimeOffset UtcNow => _utcNow;

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
        return new ExecutionTaskHandle(
            handle,
            _scheduler,
            _shutdownToken);
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
        return new ExecutionTaskHandle(
            _runtime.GetScheduledTaskHandle(taskId),
            _scheduler,
            _shutdownToken);
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
        TaskScheduler scheduler,
        CancellationToken shutdownToken) : IScheduledTaskHandle
    {
        public TaskId TaskId => inner.TaskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                scheduler,
                () => CancelCoreAsync(cancellationToken));

        private async ValueTask CancelCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdownToken);
            await inner.CancelAsync(executionCts.Token);
        }

        public ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) =>
            RunOnScheduler(
                scheduler,
                () => PollCoreAsync(options, cancellationToken));

        private async ValueTask<DurableTaskResponse> PollCoreAsync(
            PollingOptions options,
            CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdownToken);
            return await inner.PollAsync(options, executionCts.Token);
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                scheduler,
                () => WaitCoreAsync(cancellationToken));

        private async ValueTask<DurableTaskResponse> WaitCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdownToken);
            return await inner.WaitAsync(executionCts.Token);
        }
    }
}
