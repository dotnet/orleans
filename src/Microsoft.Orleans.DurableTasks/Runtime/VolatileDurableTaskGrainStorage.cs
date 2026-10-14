using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
    TimeProvider timeProvider,
    Serializer serializer) : IDurableTaskGrainStorage
{
    private readonly DeepCopier<Dictionary<TaskId, DurableTaskState>> _storageCopier = storageCopier;
    private readonly DeepCopier<DurableTaskState> _stateCopier = stateCopier;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly Serializer _serializer = serializer;
    private readonly ConditionalWeakTable<DurableTaskState, StateOwnership> _stateOwnership = [];
    private Dictionary<TaskId, DurableTaskState> _workingCopy = [];
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = [];
    private DurableTaskHierarchy _hierarchy = new();

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public void AddOrUpdateTask(TaskId taskId, DurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ArgumentNullException.ThrowIfNull(state);
        TrackState(taskId, state);
        var added = !_workingCopy.ContainsKey(taskId);
        _workingCopy[taskId] = TrackState(taskId, CopyState(state));
        if (added)
        {
            _hierarchy.Add(taskId);
        }
    }

    public bool RemoveTask(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        Debug.Assert(
            !_workingCopy.TryGetValue(taskId, out var removed) || removed.CompletionDestinations.Count == 0,
            "A pending completion retains its task's result.");
        if (!_workingCopy.Remove(taskId))
        {
            return false;
        }

        _hierarchy.Remove(taskId, IsStored);
        return true;
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = TrackState(taskId, CopyState(internalState));
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        _workingCopy = CopyStorage(_persistedCopy);
        foreach (var (taskId, state) in _workingCopy)
        {
            TrackState(taskId, state);
        }

        _hierarchy = DurableTaskHierarchy.Create(_workingCopy.Keys);
        return default;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        _persistedCopy = CopyStorage(_workingCopy);
        return default;
    }

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (!TryGetTask(taskId, out var result))
        {
            if (request is not null && request.Context is null)
            {
                throw new InvalidOperationException("The request context must not be null");
            }

            var created = new DurableTaskState { CreatedAt = _timeProvider.GetUtcNow() }.Bind(_serializer);
            created.SetRequest(request);
            result = created;

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
        var typedState = GetState(taskId, state);
        typedState.SetRequest(request);
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint)
    {
        var typedState = GetState(taskId, state);
        typedState.RequestFingerprint = fingerprint;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint)
    {
        var typedState = GetState(taskId, state);
        typedState.RemoteTarget = target;
        typedState.RemoteRequestFingerprint = fingerprint;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(callerId, default);
        var typedState = GetState(taskId, state);
        typedState.CallerId = callerId;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        var typedState = GetState(taskId, state);
        typedState.SetResult(response);
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
        var typedState = GetState(taskId, state);
        typedState.CompletionDestinations.Add(destination);
        AddOrUpdateTask(taskId, typedState);
    }

    public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(taskId, state);
        typedState.CompletionDestinations.Remove(destination);
        AddOrUpdateTask(taskId, typedState);
    }

    public void RetainResult(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        typedState.RetainedSince = _timeProvider.GetUtcNow();
        AddOrUpdateTask(taskId, typedState);
    }

    private DurableTaskState GetState(TaskId taskId, IDurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ArgumentNullException.ThrowIfNull(state);

        if (state is not DurableTaskState result
            || !_stateOwnership.TryGetValue(result, out var ownership)
            || ownership.TaskId != taskId
            || !_workingCopy.ContainsKey(taskId))
        {
            throw new ArgumentException("The provided value does not belong to this storage provider.", nameof(state));
        }

        return result;
    }

    // The copier copies the persisted members; the payloads, which journaled storage persists separately, are shared.
    private DurableTaskState CopyState(DurableTaskState state)
    {
        var copy = (_stateCopier.Copy(state)
            ?? throw new InvalidOperationException("The durable task state copier returned null.")).Bind(_serializer);
        copy.CopyPayloadsFrom(state);
        return copy;
    }

    private Dictionary<TaskId, DurableTaskState> CopyStorage(Dictionary<TaskId, DurableTaskState> storage)
    {
        var result = _storageCopier.Copy(storage)
            ?? throw new InvalidOperationException("The durable task storage copier returned null.");
        foreach (var (taskId, state) in result)
        {
            state.Bind(_serializer).CopyPayloadsFrom(storage[taskId]);
        }

        return result;
    }

    private DurableTaskState TrackState(TaskId taskId, DurableTaskState state)
    {
        if (_stateOwnership.TryGetValue(state, out var ownership))
        {
            if (ownership.TaskId != taskId)
            {
                throw new ArgumentException("The provided value is associated with a different durable task.", nameof(state));
            }

            return state;
        }

        _stateOwnership.Add(state, new StateOwnership(taskId));
        return state;
    }

    private sealed class StateOwnership(TaskId taskId)
    {
        public TaskId TaskId { get; } = taskId;
    }

    public void Clear()
    {
        _workingCopy.Clear();
        _hierarchy.Clear();
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null)
    {
        _ = GetState(taskId, state);
        if (!_workingCopy.TryGetValue(taskId, out var current)
            || current.CompletedAt.HasValue
            || current.CancellationRequestedAt.HasValue)
        {
            return;
        }

        var updated = CopyState(current);
        updated.CancellationRequestedAt = _timeProvider.GetUtcNow();
        updated.CancellationReason = reason;
        _workingCopy[taskId] = TrackState(taskId, updated);
    }

    public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation)
    {
        var typedState = GetState(taskId, state);
        typedState.DueTime = dueTime;
        typedState.ResumeGeneration = generation;
        AddOrUpdateTask(taskId, typedState);
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) =>
        _hierarchy
            .GetNearestStoredDescendants(parentId, IsStored)
            .Select(id => (id, (IDurableTaskState)_workingCopy[id]));

    private bool IsStored(TaskId taskId) => _workingCopy.ContainsKey(taskId);
}
