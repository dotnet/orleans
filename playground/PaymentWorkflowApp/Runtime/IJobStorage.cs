using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;

namespace PaymentWorkflowApp.Runtime;

public interface IJobStorage
{
    IEnumerable<(TaskId Id, JobTaskState State)> Tasks { get; }
    void AddOrUpdateTask(TaskId taskId, JobTaskState state);
    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);

    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
