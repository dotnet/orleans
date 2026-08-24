using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingCommitCoordinator
{
    private readonly AsyncLocal<HandlerState?> _currentHandler = new();

    public bool IsHandlerActive => _currentHandler.Value is { IsActive: true };

    public HandlerScope BeginHandler()
    {
        var previous = _currentHandler.Value;
        if (previous is { IsActive: true })
        {
            throw new InvalidOperationException("Durable inbox handlers cannot be nested on the same activation.");
        }

        var state = new HandlerState();
        _currentHandler.Value = state;
        return new HandlerScope(this, state, previous);
    }

    internal sealed class HandlerState
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Complete() => Volatile.Write(ref _active, 0);
    }

    public readonly struct HandlerScope(
        DurableMessagingCommitCoordinator owner,
        HandlerState state,
        HandlerState? previous) : IDisposable
    {
        public void Dispose()
        {
            state.Complete();
            owner._currentHandler.Value = previous;
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
        => _coordinator.IsHandlerActive ? ValueTask.CompletedTask : _inner.WriteStateAsync(cancellationToken);

    public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
    {
        ThrowIfHandlerActive(nameof(RevertPendingChangesAsync));
        return _inner.RevertPendingChangesAsync(cancellationToken);
    }

    public ValueTask DeleteStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfHandlerActive(nameof(DeleteStateAsync));
        return _inner.DeleteStateAsync(cancellationToken);
    }

    private void ThrowIfHandlerActive(string operation)
    {
        if (_coordinator.IsHandlerActive)
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
