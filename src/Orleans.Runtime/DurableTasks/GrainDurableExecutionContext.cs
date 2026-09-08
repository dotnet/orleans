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
    IDurableTaskContinuationScheduler? continuationScheduler = null,
    TimeProvider? timeProvider = null,
    bool supportsTaskDelegates = false) : DurableExecutionContext(taskId)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    internal Task DeactivateForActivationAsync(CancellationToken cancellationToken) => DeactivateAsync(cancellationToken);

    protected internal override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask task, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        return runtime.ScheduleChildAsync(taskId, task, cancellationToken);
    }

    protected internal override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        ThrowIfNotChildTaskId(taskId);
        return runtime.ScheduleDelayAsync(taskId, dueTime, cancellationToken);
    }

    protected internal override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return runtime.GetScheduledTaskHandle(taskId);
    }

    protected internal override TaskId CreateChildTaskId(string? name)
    {
        lock (SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                var sequenceNumber = _nextSequenceNumber++;
                return TaskId.Child($"unnamed:{sequenceNumber.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                ref var nextSequenceNumber = ref CollectionsMarshal.GetValueRefOrAddDefault(_nextChildIds ??= [], name, out _);
                var sequenceNumber = nextSequenceNumber++;
                return TaskId.Child(
                    $"named:{name.Length.ToString(CultureInfo.InvariantCulture)}:{name}:{sequenceNumber.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }

    protected internal override DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
    protected internal override bool SupportsTaskDelegates => supportsTaskDelegates;

    internal override Action WrapContinuationCore(Action continuation) =>
        continuationScheduler?.WrapContinuation(continuation) ?? continuation;

    private void ThrowIfNotChildTaskId(TaskId taskId)
    {
        if (!TaskId.IsParentOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a child of this task '{TaskId}'.");
        }
    }
}
