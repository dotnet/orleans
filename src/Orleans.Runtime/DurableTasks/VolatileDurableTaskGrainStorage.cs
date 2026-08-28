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

public class VolatileDurableTaskGrainStorage : IDurableTaskGrainStorage
{
    private readonly DeepCopier<Dictionary<TaskId, DurableTaskState>> _storageCopier;
    private readonly DeepCopier<DurableTaskState> _stateCopier;
    private readonly TimeProvider _timeProvider;
    private readonly IDurableTaskMessageTransport? _messageTransport;
    private readonly AsyncLocal<NextCommitEnlistment?> _nextCommitEnlistment = new();
    private Dictionary<TaskId, DurableTaskState> _workingCopy = [];
    private Dictionary<TaskId, DurableTaskState> _persistedCopy = [];

    public VolatileDurableTaskGrainStorage(
        DeepCopier<Dictionary<TaskId, DurableTaskState>> storageCopier,
        DeepCopier<DurableTaskState> stateCopier,
        TimeProvider timeProvider)
        : this(storageCopier, stateCopier, timeProvider, messageTransport: null)
    {
    }

    internal VolatileDurableTaskGrainStorage(
        DeepCopier<Dictionary<TaskId, DurableTaskState>> storageCopier,
        DeepCopier<DurableTaskState> stateCopier,
        TimeProvider timeProvider,
        IDurableTaskMessageTransport? messageTransport)
    {
        _storageCopier = storageCopier;
        _stateCopier = stateCopier;
        _timeProvider = timeProvider;
        _messageTransport = messageTransport;
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public IDisposable? EnlistWithNextMessageCommit()
    {
        if (_messageTransport is not IDurableTaskMessageTransaction transaction)
        {
            return null;
        }

        if (_nextCommitEnlistment.Value is { IsActive: true })
        {
            return _nextCommitEnlistment.Value;
        }

        var rollbackCopy = CopyStorage(_persistedCopy);
        var enlistment = new NextCommitEnlistment();
        _nextCommitEnlistment.Value = enlistment;
        transaction.EnlistNextCommit(
            commit: () =>
            {
                if (enlistment.TryComplete())
                {
                    _persistedCopy = CopyStorage(_workingCopy);
                }
            },
            rollback: () =>
            {
                if (enlistment.TryComplete())
                {
                    _workingCopy = rollbackCopy;
                }
            });
        enlistment.SetRollback(() => _workingCopy = rollbackCopy);
        return enlistment;
    }

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

    public async ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        if (_messageTransport is IDurableTaskMessageTransaction transaction)
        {
            if (_nextCommitEnlistment.Value is { IsActive: true })
            {
                _nextCommitEnlistment.Value.Arm();
                await _messageTransport.CommitAsync(cancellationToken);
                return;
            }

            var persistedCopy = CopyStorage(_workingCopy);
            var rollbackCopy = CopyStorage(_persistedCopy);
            if (transaction.TryEnlist(
                commit: () => _persistedCopy = persistedCopy,
                rollback: () => _workingCopy = rollbackCopy))
            {
                await _messageTransport.CommitAsync(cancellationToken);
                return;
            }

            Dictionary<TaskId, DurableTaskState>? preparedSnapshot = null;
            await transaction.CommitAsync(
                prepare: () =>
                {
                    preparedSnapshot = CopyStorage(_workingCopy);
                    return ValueTask.CompletedTask;
                },
                commit: () => _persistedCopy = preparedSnapshot!,
                rollback: static () => { },
                cancellationToken: cancellationToken);
            return;
        }

        var snapshot = CopyStorage(_workingCopy);
        if (_messageTransport is not null)
        {
            await _messageTransport.CommitAsync(cancellationToken);
        }

        _persistedCopy = snapshot;
    }

    private sealed class NextCommitEnlistment : IDisposable
    {
        private int _active = 1;
        private int _armed;
        private Action? _rollback;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool TryComplete() => Interlocked.Exchange(ref _active, 0) != 0;

        public void SetRollback(Action rollback) => _rollback = rollback;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Dispose()
        {
            if (Volatile.Read(ref _armed) == 0 && TryComplete())
            {
                _rollback?.Invoke();
            }
        }
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
            // This mirrors Orleans.Journaling.DurableTasks.DurableTaskGrainStorage.GetOrCreateTask, which commits
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

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        var typedState = GetState(state);
        typedState.Result = response;
        typedState.CompletedAt = _timeProvider.GetUtcNow();
        AddOrUpdateTask(taskId, typedState);
    }

    public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        var typedState = GetState(state);
        typedState.CompletionDestinations.Add(destination);
        AddOrUpdateTask(taskId, typedState);
    }

    public void ClearCompletionDestinations(TaskId taskId, IDurableTaskState state)
    {
        var typedState = GetState(state);
        typedState.CompletionDestinations.Clear();
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetRemoteTarget(TaskId taskId, IDurableTaskState state, GrainId target)
    {
        var typedState = GetState(state);
        typedState.RemoteTarget = target;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetPendingCancellationDestination(TaskId taskId, IDurableTaskState state, GrainId target)
    {
        var typedState = GetState(state);
        typedState.PendingCancellationDestination = target;
        AddOrUpdateTask(taskId, typedState);
    }

    public void SetCancellationTombstone(TaskId taskId, IDurableTaskState state, bool value)
    {
        var typedState = GetState(state);
        typedState.IsCancellationTombstone = value;
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
