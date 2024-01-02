using System.Distributed.DurableTasks;
using System.Globalization;
using System.Runtime.InteropServices;
namespace PaymentWorkflowApp.Runtime;

internal sealed class JobDurableExecutionContext(TaskId taskId, JobScheduler jobScheduler, JobTaskState state) : DurableExecutionContext(taskId)
{
    // The sequence number for named children.
    private Dictionary<string, int>? _nextChildIds;

    // The sequence number for unnamed children.
    private int _nextSequenceNumber = 0;

    public JobTaskState State { get; } = state;

    protected override TaskId CreateChildTaskId(string? name)
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

    protected override async ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        return await jobScheduler.InvokeAsync(taskId, taskDefinition, cancellationToken);
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
    {
        return jobScheduler.GetScheduledTaskHandle(taskId);
    }

    /*
    protected override async ValueTask SignalCancellationAsyncCore(CancellationToken cancellationToken)
    {
        await jobScheduler.SignalCancellationAsync(Id, State);
    }
    */
}
