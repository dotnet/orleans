using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.DurableTasks;

namespace Orleans.DurableTasks.Protocol;

public interface IDurableTaskGrainStorage
{
    /// <summary>Gets the stored tasks whose nearest stored ancestor is <paramref name="task"/>.</summary>
    /// <remarks>
    /// A stored task can descend from <paramref name="task"/> through identifiers which are not stored themselves, such
    /// as a combinator's operation identifier. The storage indexes the hierarchy, so the cost is proportional to the
    /// returned tasks rather than to every stored task.
    /// </remarks>
    IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task);

    IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks { get; }

    IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request);
    void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request);
    void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint);
    void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint);
    void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId);

    /// <summary>
    /// Stores the task's terminal <paramref name="response"/>, stamping its completion time and the next
    /// <see cref="IDurableTaskState.CompletionSequence"/>.
    /// </summary>
    void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response);

    /// <summary>
    /// Stores a new terminal record <paramref name="taskId"/> of an execution, such as a recorded effect or a version
    /// decision, whose <paramref name="fingerprint"/> names its kind, completed with <paramref name="response"/>.
    /// </summary>
    /// <remarks>
    /// The record is stored as <see cref="GetOrCreateTask"/>, <see cref="SetRequestFingerprint"/>, and
    /// <see cref="SetResponse"/> store it. A storage which stages a complete record in one update overrides this.
    /// </remarks>
    /// <exception cref="Exception">The response can't be stored, for example because its result doesn't serialize; nothing is stored.</exception>
    IDurableTaskState AddRecord(TaskId taskId, string fingerprint, DurableTaskResponse response)
    {
        var state = GetOrCreateTask(taskId, request: null);
        SetRequestFingerprint(taskId, state, fingerprint);
        SetResponse(taskId, state, response);
        return state;
    }

    /// <summary>Records that cancellation of a non-terminal task was requested, with the request's reason, if any.</summary>
    /// <remarks>The first request determines the reason: a task whose cancellation is recorded, or which is terminal, is unchanged.</remarks>
    void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null);

    /// <summary>Records that the task is shielded: its ancestors' durable cancellation doesn't reach it.</summary>
    void Shield(TaskId taskId, IDurableTaskState state);

    /// <summary>
    /// Records whether the host of the task which this record starts accepted the start, or rejected it.
    /// </summary>
    void SetAccepted(TaskId taskId, IDurableTaskState state, bool accepted);
    void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation);

    void AddCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination);
    void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination);

    /// <summary>Restarts the retention period of a terminal result, now.</summary>
    void RetainResult(TaskId taskId, IDurableTaskState state);

    bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state);

    /// <summary>Removes a task and its state.</summary>
    /// <remarks>The task has no completion destinations: each pending completion retains its task's result.</remarks>
    bool RemoveTask(TaskId taskId);
    void Clear();

    ValueTask WriteAsync(CancellationToken cancellationToken);
    ValueTask ReadAsync(CancellationToken cancellationToken);
}
