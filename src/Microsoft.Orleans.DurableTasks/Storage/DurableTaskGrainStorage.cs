using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Storage;

/// <summary>
/// Stores durable task state in the grain's journal.
/// </summary>
/// <remarks>
/// <para>
/// Task state is rewritten on every update, so request and result payloads are journaled separately: each payload is
/// written once when it is stored, and removed when the task is deleted. A recovered state's payloads are attached the
/// first time the storage exposes it.
/// </para>
/// <para>
/// The task hierarchy is indexed in memory. The index is built from the recovered tasks on first use, then maintained
/// as tasks are created and removed; the journal recovers once per activation, before the runtime uses the storage.
/// </para>
/// <para>
/// Each stored result is stamped with the next completion sequence. The last sequence isn't journaled: it is derived
/// from the recovered tasks on first use, as their highest sequence, so a stamp costs no journal entry of its own.
/// Deleting a terminal execution tree can lower the derived value, but the combinators which compare sequences only
/// compare tasks of a running execution, which remain stored.
/// </para>
/// </remarks>
internal sealed class DurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private readonly IDurableDictionary<TaskId, DurableTaskState> _items;
    private readonly IDurableDictionary<TaskId, byte[]> _requests;
    private readonly IDurableDictionary<TaskId, byte[]> _results;
    private readonly IJournaledStateManager _stateManager;
    private readonly TimeProvider _timeProvider;
    private readonly Serializer _serializer;
    private readonly Func<TaskId, bool> _isStored;
    private DurableTaskHierarchy? _hierarchy;
    private long? _lastCompletionSequence;

    public DurableTaskGrainStorage(
        [FromKeyedServices("$tasks")] IDurableDictionary<TaskId, DurableTaskState> items,
        [FromKeyedServices("$task-requests")] IDurableDictionary<TaskId, byte[]> requests,
        [FromKeyedServices("$task-results")] IDurableDictionary<TaskId, byte[]> results,
        IJournaledStateManager stateManager,
        TimeProvider timeProvider,
        Serializer serializer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(serializer);

        _items = items;
        _requests = requests;
        _results = results;
        _stateManager = stateManager;
        _timeProvider = timeProvider;
        _serializer = serializer;
        _isStored = items.ContainsKey;
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks =>
        _items.Select(pair => (pair.Key, (IDurableTaskState)Expose(pair.Key, pair.Value)));

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) =>
        (_hierarchy ??= DurableTaskHierarchy.Create(_items.Keys))
            .GetNearestStoredDescendants(parentId, _isStored)
            .Select(id => (id, (IDurableTaskState)Expose(id, _items[id])));

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            return Expose(taskId, result);
        }

        if (request is not null)
        {
            ValidateRequest(request);
        }

        result = new DurableTaskState { CreatedAt = _timeProvider.GetUtcNow() }.Bind(_serializer);
        result.SetRequest(request);
        _items.Add(taskId, result);
        _hierarchy?.Add(taskId);
        if (result.RequestPayload is { } payload)
        {
            _requests[taskId] = payload;
        }

        return result;
    }

    public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var typedState = GetState(taskId, state);
        typedState.SetRequest(request);
        _requests[taskId] = typedState.RequestPayload!;
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
        typedState.SetResult(response);
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        typedState.CompletionSequence = NextCompletionSequence();
        typedState.DueTime = null;
        if (typedState.ResumeGeneration > 0)
        {
            typedState.ResumeGeneration = checked(typedState.ResumeGeneration + 1);
        }
        _items[taskId] = typedState;
        _results[taskId] = typedState.ResultPayload!;
    }

    private long NextCompletionSequence()
    {
        if (_lastCompletionSequence is not { } last)
        {
            last = 0;
            foreach (var (_, state) in _items)
            {
                last = Math.Max(last, state.CompletionSequence);
            }
        }

        _lastCompletionSequence = ++last;
        return last;
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null)
    {
        var typedState = GetState(taskId, state);
        if (typedState.CancellationRequestedAt.HasValue || typedState.CompletedAt.HasValue)
        {
            return;
        }

        typedState.CancellationRequestedAt = _timeProvider.GetUtcNow();
        typedState.CancellationReason = reason;
        _items[taskId] = typedState;
    }

    public void SuppressCancellation(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        typedState.CancellationSuppressed = true;
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

    public void RetainResult(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(taskId, state);
        typedState.RetainedSince = _timeProvider.GetUtcNow();
        _items[taskId] = typedState;
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var result))
        {
            state = Expose(taskId, result);
            return true;
        }

        state = null;
        return false;
    }

    public bool RemoveTask(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        Debug.Assert(
            !_items.TryGetValue(taskId, out var removed) || removed.CompletionDestinations.Count == 0,
            "A pending completion retains its task's result.");
        _requests.Remove(taskId);
        _results.Remove(taskId);
        if (!_items.Remove(taskId))
        {
            return false;
        }

        _hierarchy?.Remove(taskId, _isStored);
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _requests.Clear();
        _results.Clear();
        _hierarchy = null;
        _lastCompletionSequence = null;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken) =>
        _stateManager.WriteStateAsync(cancellationToken);

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        _hierarchy = null;
        _lastCompletionSequence = null;
        return _stateManager.InitializeAsync(cancellationToken);
    }

    private static void ValidateRequest(IDurableTaskRequest request)
    {
        if (request.Context is null)
        {
            throw new InvalidOperationException("The request context must not be null.");
        }
    }

    private DurableTaskState Expose(TaskId taskId, DurableTaskState state)
    {
        state.Bind(_serializer);
        if (!state.PayloadsAttached)
        {
            state.AttachPayloads(
                _requests.TryGetValue(taskId, out var request) ? request : null,
                _results.TryGetValue(taskId, out var result) ? result : null);
        }

        return state;
    }

    private DurableTaskState GetState(TaskId taskId, IDurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ArgumentNullException.ThrowIfNull(state);

        if (state is not DurableTaskState result
            || !_items.TryGetValue(taskId, out var current)
            || !ReferenceEquals(current, result))
        {
            throw new ArgumentException("The provided value does not belong to this storage provider.", nameof(state));
        }

        return result;
    }
}
