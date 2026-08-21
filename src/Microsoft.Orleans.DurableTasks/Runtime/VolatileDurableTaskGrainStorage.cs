#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

public class VolatileDurableTaskGrainStorage(
    DeepCopier<Dictionary<TaskId, DurableTaskState>> storageCopier,
    DeepCopier<DurableTaskState> stateCopier,
    TimeProvider timeProvider) : IDurableTaskGrainStorage
{
    private readonly DeepCopier<Dictionary<TaskId, DurableTaskState>> _storageCopier = storageCopier;
    private readonly DeepCopier<DurableTaskState> _stateCopier = stateCopier;
    private readonly TimeProvider _timeProvider = timeProvider;
    private Dictionary<TaskId, DurableTaskState> _workingCopy = [];
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = [];

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public void AddOrUpdateTask(TaskId taskId, DurableTaskState state) => _workingCopy[taskId] = CopyState(state);
    public bool RemoveTask(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = CopyState(internalState);
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        _workingCopy = CopyStorage(_persistedCopy);
        return default;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        _persistedCopy = CopyStorage(_workingCopy);
        return default;
    }

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        if (!TryGetTask(taskId, out var result))
        {
            if (request is not null && request.Context is null)
            {
                throw new InvalidOperationException("The request context must not be null");
            }

            result = new DurableTaskState
            {
                Request = request,
                CreatedAt = _timeProvider.GetUtcNow(),
            };

            // Persist the newly-created state immediately so that it is visible to subsequent TryGetTask/SetResponse
            // calls even if no further Set* mutation is made on this instance (e.g. when 'request' is already
            // non-null, the "state.Request is null" fast-path used by some callers to decide whether to call
            // SetRequest will not fire, and this state would otherwise never be committed to the working copy).
            // This mirrors Orleans.DurableTasks.Storage.DurableTaskGrainStorage.GetOrCreateTask, which commits
            // the newly-created state via _items.Add(taskId, result) immediately.
            AddOrUpdateTask(taskId, (DurableTaskState)result);
        }

        return result;
    }

    public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request)
    {
        var typedState = GetState(state);
        typedState.Request = request;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint)
    {
        var typedState = GetState(state);
        typedState.RequestFingerprint = fingerprint;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint)
    {
        var typedState = GetState(state);
        typedState.RemoteTarget = target;
        typedState.RemoteRequestFingerprint = fingerprint;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(callerId, default);
        var typedState = GetState(state);
        typedState.CallerId = callerId;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        var typedState = GetState(state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        typedState.DueTime = null;
        if (typedState.ResumeGeneration > 0)
        {
            typedState.ResumeGeneration = checked(typedState.ResumeGeneration + 1);
        }
        AddOrUpdateTask(taskId, typedState);
    }

    public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(state);
        typedState.CompletionDestinations.Add(destination);
        AddOrUpdateTask(taskId, typedState);
    }

    public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(state);
        typedState.CompletionDestinations.Remove(destination);
        AddOrUpdateTask(taskId, typedState);
    }

    public void CreateTombstone(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(state);
        typedState.Request = null;
        typedState.Result = null;
        typedState.TombstonedAt = _timeProvider.GetUtcNow();
        AddOrUpdateTask(taskId, typedState);
    }

    private static DurableTaskState GetState(IDurableTaskState state)
    {
        if (state is not DurableTaskState result)
        {
            throw new ArgumentException("The provided value does not belong to this storage provider", nameof(state));
        }

        return result;
    }

    private DurableTaskState CopyState(DurableTaskState state) =>
        _stateCopier.Copy(state)
        ?? throw new InvalidOperationException("The durable task state copier returned null.");

    private Dictionary<TaskId, DurableTaskState> CopyStorage(Dictionary<TaskId, DurableTaskState> storage) =>
        _storageCopier.Copy(storage)
        ?? throw new InvalidOperationException("The durable task storage copier returned null.");


    public void Clear()
    {
        _workingCopy.Clear();
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state)
    {
        _ = GetState(state);
        if (!_workingCopy.TryGetValue(taskId, out var current)
            || current.CompletedAt.HasValue
            || current.CancellationRequestedAt.HasValue)
        {
            return;
        }

        var updated = CopyState(current);
        updated.CancellationRequestedAt = _timeProvider.GetUtcNow();
        _workingCopy[taskId] = updated;
    }

    public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation)
    {
        var typedState = GetState(state);
        typedState.DueTime = dueTime;
        typedState.ResumeGeneration = generation;
        AddOrUpdateTask(taskId, typedState);
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) => Tasks.Where(task => parentId.IsParentOf(task.Id));
}
