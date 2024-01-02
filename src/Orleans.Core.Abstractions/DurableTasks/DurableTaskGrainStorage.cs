using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableTasks;

public interface IDurableTaskGrainStorage
{
    IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task);

    IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks { get; }

    IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request);
    void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response);
    void RequestCancellation(TaskId taskId, IDurableTaskState state);

    void AddObserver(TaskId taskId, IDurableTaskState state, IDurableTaskObserver observer);
    void ClearObservers(TaskId taskId, IDurableTaskState state);

    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    void Clear();

    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
