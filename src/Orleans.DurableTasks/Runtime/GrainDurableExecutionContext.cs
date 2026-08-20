#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed class GrainDurableExecutionContext(
    TaskId taskId,
    IDurableTaskGrainRuntime runtime,
    CancellationToken shutdownToken) : DurableExecutionContext(taskId)
{
    private readonly CancellationToken _shutdownToken = shutdownToken;

    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;
    private readonly object _idLock = new();

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    public override DateTimeOffset UtcNow => runtime.UtcNow;

    protected override async ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(
        TaskId taskId,
        DurableTask task,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var handle = await runtime.ScheduleChildAsync(taskId, task, executionCts.Token);
        return new ExecutionTaskHandle(handle, _shutdownToken);
    }

    protected override async ValueTask<DurableTaskResponse> ScheduleDelayAsync(
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
    {
        ThrowIfNotChildTaskId(taskId);
        return new ExecutionTaskHandle(runtime.GetScheduledTaskHandle(taskId), _shutdownToken);
    }

    protected override async ValueTask<TaskId> SelectCompletionAsync(
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
        if (!TaskId.IsParentOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a child of this task '{TaskId}'.");
        }
    }

    private CancellationTokenSource CreateExecutionCancellationSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);

    private sealed class ExecutionTaskHandle(
        IScheduledTaskHandle inner,
        CancellationToken shutdownToken) : IScheduledTaskHandle
    {
        public TaskId TaskId => inner.TaskId;

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            await inner.CancelAsync(executionCts.Token);
        }

        public async ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            return await inner.PollAsync(options, executionCts.Token);
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            return await inner.WaitAsync(executionCts.Token);
        }
    }
}
