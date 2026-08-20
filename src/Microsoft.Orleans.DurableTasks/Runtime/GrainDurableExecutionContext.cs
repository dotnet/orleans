#nullable enable
using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.DurableTasks.Runtime;

internal sealed class GrainDurableExecutionContext(
    TaskId taskId,
    IDurableTaskGrainRuntime runtime,
    TaskScheduler scheduler,
    CancellationToken shutdownToken) : DurableExecutionContext(taskId)
{
    private readonly TaskScheduler _scheduler = scheduler;
    private readonly CancellationToken _shutdownToken = shutdownToken;

    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;
    private readonly object _idLock = new();

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    public override DateTimeOffset UtcNow => runtime.UtcNow;

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
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var handle = await runtime.ScheduleChildAsync(taskId, task, executionCts.Token);
        return new ExecutionTaskHandle(handle, _scheduler, _shutdownToken);
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

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await runtime.ScheduleDelayAsync(taskId, dueTime, executionCts.Token);
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
        => RunOnScheduler(
            _scheduler,
            () => GetChildTaskHandleCore(taskId));

    private IScheduledTaskHandle GetChildTaskHandleCore(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return new ExecutionTaskHandle(runtime.GetScheduledTaskHandle(taskId), _scheduler, _shutdownToken);
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
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await runtime.SelectCompletionAsync(decisionId, candidates, executionCts.Token);
    }

    protected override TaskId CreateChildTaskId(string? name)
    {
        lock (_idLock)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                var sequenceNumber = _nextSequenceNumber++;
                return TaskId.Child(sequenceNumber.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                ref var nextSequenceNumber = ref CollectionsMarshal.GetValueRefOrAddDefault(_nextChildIds ??= [], name, out _);
                var sequenceNumber = nextSequenceNumber++;
                if (sequenceNumber > 0)
                {
                    return TaskId.Child($"{name}.{sequenceNumber.ToString(CultureInfo.InvariantCulture)}");
                }

                return TaskId.Child(name);
            }
        }
    }

    private void ThrowIfNotChildTaskId(TaskId taskId)
    {
        if (!TaskId.IsAncestorOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a descendant of this task '{TaskId}'.");
        }
    }

    private CancellationTokenSource CreateExecutionCancellationSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);

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
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
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
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            return await inner.PollAsync(options, executionCts.Token);
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                scheduler,
                () => WaitCoreAsync(cancellationToken));

        private async ValueTask<DurableTaskResponse> WaitCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            return await inner.WaitAsync(executionCts.Token);
        }
    }
}
