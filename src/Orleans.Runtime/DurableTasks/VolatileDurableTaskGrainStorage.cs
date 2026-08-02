#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.DurableTasks;
using Orleans.Serialization;

namespace Orleans.Runtime.DurableTasks;

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
            if (request?.Context is null)
            {
                throw new InvalidOperationException("The request context must not be null");
            }

            result = new DurableTaskState
            {
                Request = request,
                CreatedAt = _timeProvider.GetUtcNow(),
            };
        }

        return result;
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        var typedState = GetState(state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        AddOrUpdateTask(taskId, typedState);
    }

    public void AddObserver(TaskId taskId, IDurableTaskState state, IDurableTaskObserver observer)
    {
        var typedState = GetState(state);
        var clients = typedState.Observers ??= [];
        clients.Add(observer);
        AddOrUpdateTask(taskId, typedState);
    }

    public void ClearObservers(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(state);
        typedState.Observers?.Clear();
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
        var typedState = GetState(state);
        typedState.CancellationRequestedAt = _timeProvider.GetUtcNow();
        AddOrUpdateTask(taskId, typedState);
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) => Tasks.Where(task => parentId.IsParentOf(task.Id));
}
