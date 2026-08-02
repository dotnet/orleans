using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;

namespace Orleans.Journaling.DurableTasks;

internal sealed class DurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private readonly IDurableDictionary<TaskId, DurableTaskState> _items;
    private readonly IJournaledStateManager _stateManager;
    private readonly TimeProvider _timeProvider;

    public DurableTaskGrainStorage(
        [FromKeyedServices("$tasks")] IDurableDictionary<TaskId, DurableTaskState> items,
        IJournaledStateManager stateManager,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _items = items;
        _stateManager = stateManager;
        _timeProvider = timeProvider;
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
        _items.Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) =>
        _items
            .Where(pair => parentId.IsParentOf(pair.Key))
            .Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            return result;
        }

        if (request?.Context is null)
        {
            throw new InvalidOperationException("The request context must not be null.");
        }

        result = new DurableTaskState
        {
            Request = request,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        _items.Add(taskId, result);

        return result;
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var typedState = GetState(taskId, state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        if (typedState.CancellationRequestedAt.HasValue || typedState.CompletedAt.HasValue)
        {
            return;
        }

        typedState.CancellationRequestedAt = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public void AddObserver(TaskId taskId, IDurableTaskState state, IDurableTaskObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var typedState = GetState(taskId, state);
        if (typedState.Observers.Add(observer))
        {
            _items[taskId] = typedState;
        }
    }

    public void ClearObservers(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        if (typedState.Observers.Count > 0)
        {
            typedState.Observers.Clear();
            _items[taskId] = typedState;
        }
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            state = result;
            return true;
        }

        state = null;
        return false;
    }

    public bool RemoveTask(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return _items.Remove(taskId);
    }

    public void Clear() => _items.Clear();

    public ValueTask WriteAsync(CancellationToken cancellationToken) =>
        _stateManager.WriteStateAsync(cancellationToken);

    public ValueTask ReadAsync(CancellationToken cancellationToken) =>
        _stateManager.InitializeAsync(cancellationToken);

    private DurableTaskState GetState(TaskId taskId, IDurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ArgumentNullException.ThrowIfNull(state);

        if (state is not DurableTaskState result || !_items.ContainsKey(taskId))
        {
            throw new ArgumentException("The provided value does not belong to this storage provider.", nameof(state));
        }

        return result;
    }
}
