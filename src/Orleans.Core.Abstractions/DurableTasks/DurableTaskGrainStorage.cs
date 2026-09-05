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
    void SetRemoteTarget(TaskId taskId, IDurableTaskState state, GrainId target);
    void SetPendingCancellationDestination(TaskId taskId, IDurableTaskState state, GrainId target);
    void SetCancellationTombstone(TaskId taskId, IDurableTaskState state, bool value);
    void SetTaskKind(TaskId taskId, IDurableTaskState state, DurableTaskKind kind);

    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state);

    // Removes a request and its state
    bool RemoveTask(TaskId taskId);
    void Clear();

    /// <summary>
    /// Enlists the current working state in the next durable messaging commit on this async flow.
    /// </summary>
    /// <returns><see langword="true"/> when the storage snapshot was enlisted.</returns>
    IDisposable? EnlistWithNextMessageCommit() => null;

    /// <summary>
    /// Gets a value indicating whether writes by activation journal participants include this storage.
    /// </summary>
    bool IsCommitIntegratedWithActivationJournal => false;

    /// <summary>
    /// Atomically commits task state together with durable messaging effects staged by the current activation.
    /// </summary>
    /// <remarks>
    /// Implementations used with durable messaging must share its transaction boundary. Returning success guarantees
    /// that both the task-state mutation and its staged invocation, completion, or cancellation envelopes are durable.
    /// </remarks>
    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
