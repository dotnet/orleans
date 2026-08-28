using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;

namespace Orleans.Journaling.DurableTasks;

internal sealed class DurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private readonly IDurableDictionary<TaskId, DurableTaskState> _items;
    private readonly IJournaledStateManager _stateManager;
    private readonly TimeProvider _timeProvider;
    private readonly DeepCopier<DurableTaskState> _stateCopier;

    public bool IsCommitIntegratedWithActivationJournal => true;

    public DurableTaskGrainStorage(
        [FromKeyedServices("$tasks")] IDurableDictionary<TaskId, DurableTaskState> items,
        IJournaledStateManager stateManager,
        TimeProvider timeProvider,
        DeepCopier<DurableTaskState> stateCopier)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(stateCopier);

        _items = items;
        _stateManager = stateManager;
        _timeProvider = timeProvider;
        _stateCopier = stateCopier;
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
        _items.Select(pair => (pair.Key, (IDurableTaskState)CopyStoredState(pair.Value)));

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) =>
        _items
            .Where(pair => parentId.IsParentOf(pair.Key))
            .Select(pair => (pair.Key, (IDurableTaskState)CopyStoredState(pair.Value)));

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            var copy = CopyStoredState(result);
            if (copy.MigrateLegacyObservers())
            {
                _items[taskId] = CopyStoredState(copy);
            }

            return copy;
        }

        if (request is not null && request.Context is null)
        {
            throw new InvalidOperationException("The request context must not be null.");
        }

        result = new DurableTaskState
        {
            Request = request,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        _items.Add(taskId, CopyStoredState(result));

        return result;
    }

    public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var typedState = CopyState(taskId, state);
        typedState.Request = request;
        _items[taskId] = typedState;
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var typedState = CopyState(taskId, state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state)
    {
        var typedState = CopyState(taskId, state);
        if (typedState.CancellationRequestedAt.HasValue || typedState.CompletedAt.HasValue)
        {
            return;
        }

        typedState.CancellationRequestedAt = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = CopyState(taskId, state);
        if (typedState.CompletionDestinations.Add(destination))
        {
            _items[taskId] = typedState;
        }
    }

    public void ClearCompletionDestinations(TaskId taskId, IDurableTaskState state)
    {
        var typedState = CopyState(taskId, state);
        if (typedState.CompletionDestinations.Count > 0)
        {
            typedState.CompletionDestinations.Clear();
            _items[taskId] = typedState;
        }
    }

    public void SetRemoteTarget(TaskId taskId, IDurableTaskState state, GrainId target)
    {
        var typedState = CopyState(taskId, state);
        typedState.RemoteTarget = target;
        _items[taskId] = typedState;
    }

    public void SetPendingCancellationDestination(TaskId taskId, IDurableTaskState state, GrainId target)
    {
        var typedState = CopyState(taskId, state);
        typedState.PendingCancellationDestination = target;
        _items[taskId] = typedState;
    }

    public void SetCancellationTombstone(TaskId taskId, IDurableTaskState state, bool value)
    {
        var typedState = CopyState(taskId, state);
        typedState.IsCancellationTombstone = value;
        _items[taskId] = typedState;
    }

    public void SetTaskKind(TaskId taskId, IDurableTaskState state, DurableTaskKind kind)
    {
        var typedState = CopyState(taskId, state);
        typedState.Kind = kind;
        _items[taskId] = typedState;
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            var copy = CopyStoredState(result);
            if (copy.MigrateLegacyObservers())
            {
                _items[taskId] = CopyStoredState(copy);
            }

            state = copy;
            return true;
        }

        state = null;
        return false;
    }

    private DurableTaskState CopyState(TaskId taskId, IDurableTaskState state)
    {
        var copy = CopyStoredState(GetState(taskId, state));
        _ = copy.MigrateLegacyObservers();
        return copy;
    }

    private DurableTaskState CopyStoredState(DurableTaskState state) =>
        _stateCopier.Copy(state)
        ?? throw new InvalidOperationException("The durable task state copier returned null.");

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

        if (state is not DurableTaskState || !_items.TryGetValue(taskId, out var result))
        {
            throw new ArgumentException("The provided value does not belong to this storage provider.", nameof(state));
        }

        return result;
    }
}
