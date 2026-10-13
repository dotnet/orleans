using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.DurableTasks;

namespace Orleans.DurableTasks.Protocol;

public interface IDurableTaskGrainStorage
{
    IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task);

    IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks { get; }

    IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request);
    void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request);
    void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint);
    void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint);
    void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId);
    void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response);
    void RequestCancellation(TaskId taskId, IDurableTaskState state);
    void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, TimeSpan duration, long generation);

    void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination);
    void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination);

    /// <summary>Restarts the retention period of a terminal result, now.</summary>
    void RetainResult(TaskId taskId, IDurableTaskState state);

    /// <summary>Replaces a task's request and result with a tombstone which retains its identity.</summary>
    /// <remarks>The task has no completion destinations: each pending completion retains its task's result.</remarks>
    void CreateTombstone(TaskId taskId, IDurableTaskState state);

    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state);

    /// <summary>Removes a task and its state.</summary>
    /// <remarks>The task has no completion destinations: each pending completion retains its task's result.</remarks>
    bool RemoveTask(TaskId taskId);
    void Clear();

    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
