using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Storage;

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
            if (result.MigrateLegacyObservers())
            {
                _items[taskId] = result;
            }

            return result;
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
        _items.Add(taskId, result);

        return result;
    }

    public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var typedState = GetState(taskId, state);
        typedState.Request = request;
        _items[taskId] = typedState;
    }

    public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var typedState = GetState(taskId, state);
        typedState.RequestFingerprint = fingerprint;
        _items[taskId] = typedState;
    }

    public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string fingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(target, default);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var typedState = GetState(taskId, state);
        typedState.RemoteTarget = target;
        typedState.RemoteRequestFingerprint = fingerprint;
        _items[taskId] = typedState;
    }

    public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(callerId, default);
        var typedState = GetState(taskId, state);
        typedState.CallerId = callerId;
        _items[taskId] = typedState;
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var typedState = GetState(taskId, state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        typedState.DueTime = null;
        if (typedState.ResumeGeneration > 0)
        {
            typedState.ResumeGeneration = checked(typedState.ResumeGeneration + 1);
        }
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

    public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation)
    {
        var typedState = GetState(taskId, state);
        typedState.DueTime = dueTime;
        typedState.ResumeGeneration = generation;
        _items[taskId] = typedState;
    }

    public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(taskId, state);
        if (typedState.CompletionDestinations.Add(destination))
        {
            _items[taskId] = typedState;
        }
    }

    public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(taskId, state);
        if (typedState.CompletionDestinations.Remove(destination))
        {
            _items[taskId] = typedState;
        }
    }

    public void CreateTombstone(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        typedState.Request = null;
        typedState.Result = null;
        typedState.TombstonedAt = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            if (result.MigrateLegacyObservers())
            {
                _items[taskId] = result;
            }

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

        if (result.MigrateLegacyObservers())
        {
            _items[taskId] = result;
        }

        return result;
    }
}
