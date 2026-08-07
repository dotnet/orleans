using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

public interface IDurableTaskGrainStorage
{
    IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task);

    IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks { get; }

    IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request);
    void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request);
    void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response);
    void RequestCancellation(TaskId taskId, IDurableTaskState state);

    void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination);
    void ClearCompletionDestinations(TaskId taskId, IDurableTaskState state);

    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    void Clear();

    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
