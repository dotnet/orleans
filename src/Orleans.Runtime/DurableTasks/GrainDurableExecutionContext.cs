#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed class GrainDurableExecutionContext(TaskId taskId, IDurableTaskGrainRuntime runtime) : DurableExecutionContext(taskId)
{
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    protected override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask task, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        return runtime.ScheduleChildAsync(taskId, task, cancellationToken);
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return runtime.GetScheduledTaskHandle(taskId);
    }

    protected override TaskId CreateChildTaskId(string? name)
    {
        lock (SyncRoot)
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
}
