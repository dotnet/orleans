using System.Diagnostics.CodeAnalysis;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingCommitCoordinator :
    IDurableJobTurnIsolationReentrantScope,
    IJournaledStateMutationGuard
{
    private readonly AsyncLocal<HandlerState?> _currentHandler = new();
    private readonly AsyncLocal<ParticipantBatch?> _nextCommitParticipants = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private HandlerState? _activeHandler;
    private long _nextHandlerSequence;
    private long _lastCompletedHandlerSequence;
    private long _lastRolledBackHandlerSequence;

    public bool IsHandlerActive => _currentHandler.Value is { IsActive: true };
    public bool HasActiveHandler => Volatile.Read(ref _activeHandler) is { IsActive: true };

    public void ThrowIfMutationBlocked()
    {
        if (HasActiveHandler && !IsHandlerActive)
        {
            throw new InvalidOperationException(
                "Durable state cannot be mutated by an unrelated execution while an inbox transaction is active.");
        }
    }

    public async ValueTask<HandlerScope> BeginHandlerAsync(CancellationToken cancellationToken)
    {
        if (_currentHandler.Value is { IsActive: true })
        {
            throw new InvalidOperationException("Durable inbox handlers cannot be nested on the same activation.");
        }

        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var state = new HandlerState(Interlocked.Increment(ref _nextHandlerSequence));
        if (Interlocked.CompareExchange(ref _activeHandler, state, comparand: null) is not null)
        {
            _commitGate.Release();
            throw new InvalidOperationException("A durable inbox handler is already active on this activation.");
        }

        return new HandlerScope(this, state);
    }

    public async ValueTask ExecuteExclusiveAsync(
        Func<ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var completedBeforeWait = Volatile.Read(ref _lastCompletedHandlerSequence);
        var participants = _nextCommitParticipants.Value?.Consume();
        try
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CompleteParticipants(participants, committed: false);
            throw;
        }

        try
        {
            if (Volatile.Read(ref _lastRolledBackHandlerSequence) > completedBeforeWait)
            {
                throw new InvalidOperationException(
                    "A durable inbox transaction rolled back before this write could be committed.");
            }

            await operation().ConfigureAwait(false);
            CompleteParticipants(participants, committed: true);
        }
        catch
        {
            CompleteParticipants(participants, committed: false);
            throw;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private static void CompleteParticipants(
        List<(Action Commit, Action Rollback)>? participants,
        bool committed)
    {
        if (participants is null)
        {
            return;
        }

        foreach (var participant in participants)
        {
            (committed ? participant.Commit : participant.Rollback)();
        }
    }

    private sealed class ParticipantBatch
    {
        private readonly object _sync = new();
        private List<(Action Commit, Action Rollback)>? _participants = [];

        public bool IsConsumed
        {
            get
            {
                lock (_sync)
                {
                    return _participants is null;
                }
            }
        }

        public void Add(Action commit, Action rollback)
        {
            lock (_sync)
            {
                (_participants ?? throw new InvalidOperationException("The commit participant batch was already consumed."))
                    .Add((commit, rollback));
            }
        }

        public List<(Action Commit, Action Rollback)>? Consume()
        {
            lock (_sync)
            {
                var result = _participants;
                _participants = null;
                return result;
            }
        }
    }

    public IDisposable JoinReentrantScope()
    {
        var state = Volatile.Read(ref _activeHandler);
        if (state is not { IsActive: true })
        {
            return ReentrantScope.None;
        }

        var previous = _currentHandler.Value;
        _currentHandler.Value = state;
        return new ReentrantScope(this, previous);
    }

    public bool TryEnlist(Action commit, Action rollback)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(rollback);
        if (_currentHandler.Value is not { IsActive: true } state)
        {
            return false;
        }

        state.Enlist(commit, rollback);
        return true;
    }

    public void EnlistNextCommit(Action commit, Action rollback)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(rollback);
        if (TryEnlist(commit, rollback))
        {
            return;
        }

        var batch = _nextCommitParticipants.Value;
        if (batch is null || batch.IsConsumed)
        {
            batch = new ParticipantBatch();
            _nextCommitParticipants.Value = batch;
        }

        batch.Add(commit, rollback);
    }

    internal sealed class HandlerState(long sequence)
    {
        private int _active = 1;
        private readonly object _sync = new();
        private List<(Action Commit, Action Rollback)>? _participants;

        public bool IsActive => Volatile.Read(ref _active) != 0;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Sequence { get; } = sequence;

        public void Complete(bool committed)
        {
            Volatile.Write(ref _active, 0);
            List<(Action Commit, Action Rollback)>? participants;
            lock (_sync)
            {
                participants = _participants;
                _participants = null;
            }

            if (participants is not null)
            {
                foreach (var participant in participants)
                {
                    (committed ? participant.Commit : participant.Rollback)();
                }
            }

            Completion.TrySetResult(committed);
        }

        public void Enlist(Action commit, Action rollback)
        {
            lock (_sync)
            {
                if (!IsActive)
                {
                    throw new InvalidOperationException("The durable inbox transaction has already completed.");
                }

                (_participants ??= []).Add((commit, rollback));
            }
        }
    }

    public sealed class HandlerScope(
        DurableMessagingCommitCoordinator owner,
        HandlerState state) : IDisposable
    {
        private int _completed;
        private HandlerState? _previous;
        private bool _activated;

        public void Activate()
        {
            if (_activated)
            {
                return;
            }

            _previous = owner._currentHandler.Value;
            if (_previous is { IsActive: true })
            {
                throw new InvalidOperationException("Durable inbox handlers cannot be nested on the same activation.");
            }

            owner._currentHandler.Value = state;
            _activated = true;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                Volatile.Write(ref owner._lastCompletedHandlerSequence, state.Sequence);
                _ = Interlocked.CompareExchange(ref owner._activeHandler, null, state);
                if (_activated)
                {
                    owner._currentHandler.Value = _previous;
                }
                state.Complete(committed: true);
                owner._commitGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                Volatile.Write(ref owner._lastRolledBackHandlerSequence, state.Sequence);
                Volatile.Write(ref owner._lastCompletedHandlerSequence, state.Sequence);
                _ = Interlocked.CompareExchange(ref owner._activeHandler, null, state);
                if (_activated)
                {
                    owner._currentHandler.Value = _previous;
                }
                state.Complete(committed: false);
                owner._commitGate.Release();
            }
        }
    }

    private sealed class ReentrantScope(
        DurableMessagingCommitCoordinator? owner,
        HandlerState? previous) : IDisposable
    {
        public static ReentrantScope None { get; } = new(null, null);

        public void Dispose()
        {
            if (owner is not null)
            {
                owner._currentHandler.Value = previous;
            }
        }
    }
}

internal sealed class CoordinatedJournaledStateManager :
    IJournaledStateManager,
    ILifecycleParticipant<IGrainLifecycle>,
    IDisposable
{
    private readonly IJournaledStateManager _inner;
    private readonly DurableMessagingCommitCoordinator _coordinator;
    private int _disposed;

    public CoordinatedJournaledStateManager(
        IJournaledStateManagerFactory factory,
        IGrainContext grainContext,
        DurableMessagingCommitCoordinator coordinator)
        : this(factory.Create(JournalId.FromGrainId(grainContext.GrainId)), coordinator)
    {
    }

    internal CoordinatedJournaledStateManager(
        IJournaledStateManager inner,
        DurableMessagingCommitCoordinator coordinator)
    {
        _inner = inner;
        _coordinator = coordinator;
        if (inner is JournaledStateManager manager)
        {
            manager.SetMutationGuard(coordinator);
        }
    }

    public long PendingWriteByteCount => _inner.PendingWriteByteCount;

    public void Participate(IGrainLifecycle lifecycle)
    {
        if (_inner is ILifecycleParticipant<IGrainLifecycle> participant)
        {
            participant.Participate(lifecycle);
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => _inner.InitializeAsync(cancellationToken);

    public void RegisterState(string name, IJournaledState state) => _inner.RegisterState(name, state);

    public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state) => _inner.TryGetState(name, out state);

    public ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        if (_coordinator.IsHandlerActive)
        {
            return ValueTask.CompletedTask;
        }

        return WriteAfterActiveHandlerAsync(cancellationToken);
    }

    private async ValueTask WriteAfterActiveHandlerAsync(CancellationToken cancellationToken)
    {
        await _coordinator.ExecuteExclusiveAsync(
            () => _inner.WriteStateAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask CommitHandlerAsync(CancellationToken cancellationToken) =>
        _inner.WriteStateAsync(cancellationToken);

    internal ValueTask RollbackHandlerAsync(CancellationToken cancellationToken) =>
        _inner.RevertPendingChangesAsync(cancellationToken);

    public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
    {
        ThrowIfHandlerActive(nameof(RevertPendingChangesAsync));
        return _coordinator.ExecuteExclusiveAsync(
            () => _inner.RevertPendingChangesAsync(cancellationToken),
            cancellationToken);
    }

    public ValueTask DeleteStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfHandlerActive(nameof(DeleteStateAsync));
        return _coordinator.ExecuteExclusiveAsync(
            () => _inner.DeleteStateAsync(cancellationToken),
            cancellationToken);
    }

    private void ThrowIfHandlerActive(string operation)
    {
        if (_coordinator.HasActiveHandler)
        {
            throw new InvalidOperationException(
                $"{operation} cannot run while a durable inbox handler is staging an atomic completion.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else
        {
            _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _inner.DisposeAsync();
    }
}

internal sealed class UncoordinatedJournaledStateManager(IJournaledStateManager value)
{
    public IJournaledStateManager Value { get; } = value;
}

internal sealed class DurableMessagingStateManagerRegistration;

internal interface IDurableInboxFaultInjector
{
    void OnPhase(DurableInboxPersistencePhase phase);
}

internal enum DurableInboxPersistencePhase
{
    HandlerCompleted,
    CompletionStaged,
    CompletionCommitted
}
