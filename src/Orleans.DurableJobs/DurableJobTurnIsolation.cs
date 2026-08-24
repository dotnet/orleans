using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed class DurableJobTurnIsolation
{
    internal const string RequestContextKey = "Orleans.DurableJobs.TurnIsolation";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ownerId;
    private int _enabled;
    private int _leaseCount;

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal bool IsCurrentOwner
    {
        get
        {
            lock (_sync)
            {
                return IsCurrentOwnerUnderLock();
            }
        }
    }

    public void Enable() => Volatile.Write(ref _enabled, 1);

    public async ValueTask<Lease> EnterIsolatedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (IsCurrentOwnerUnderLock())
            {
                _leaseCount++;
                return new Lease(this, _ownerId!, RequestContext.Get(RequestContextKey));
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        var ownerId = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _ownerId = ownerId;
            _leaseCount = 1;
        }

        return new Lease(this, ownerId, RequestContext.Get(RequestContextKey));
    }

    public async ValueTask<Lease> EnterOrdinaryAsync()
    {
        if (!IsEnabled)
        {
            return Lease.None;
        }

        lock (_sync)
        {
            if (IsCurrentOwnerUnderLock())
            {
                _leaseCount++;
                return new Lease(this, _ownerId!, RequestContext.Get(RequestContextKey));
            }
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        var ownerId = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _ownerId = ownerId;
            _leaseCount = 1;
        }

        return new Lease(this, ownerId, RequestContext.Get(RequestContextKey));
    }

    private bool IsCurrentOwnerUnderLock() =>
        _leaseCount > 0
        && _ownerId is { } ownerId
        && string.Equals(RequestContext.Get(RequestContextKey) as string, ownerId, StringComparison.Ordinal);

    private void Release()
    {
        var releaseGate = false;
        lock (_sync)
        {
            if (--_leaseCount == 0)
            {
                _ownerId = null;
                releaseGate = true;
            }
        }

        if (releaseGate)
        {
            _gate.Release();
        }
    }

    public sealed class Lease : IDisposable
    {
        public static Lease None { get; } = new(null, null, null);

        private readonly DurableJobTurnIsolation? _owner;
        private readonly string? _ownerId;
        private readonly object? _previousOwner;
        private bool _activated;
        private int _disposed;

        internal Lease(DurableJobTurnIsolation? owner, string? ownerId, object? previousOwner)
        {
            _owner = owner;
            _ownerId = ownerId;
            _previousOwner = previousOwner;
        }

        public void Activate()
        {
            if (_owner is not null && !_activated)
            {
                RequestContext.Set(RequestContextKey, _ownerId!);
                _activated = true;
            }
        }

        public void Dispose()
        {
            if (_owner is null || Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_activated && _previousOwner is not null)
            {
                RequestContext.Set(RequestContextKey, _previousOwner);
            }
            else if (_activated)
            {
                RequestContext.Remove(RequestContextKey);
            }

            _owner.Release();
        }
    }
}

internal sealed class DurableJobTurnIsolationFilter : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.TargetId.IsSystemTarget()
            || context.InterfaceMethod.DeclaringType == typeof(IDurableJobFeatureReceiverExtension))
        {
            await context.Invoke();
            return;
        }

        var isolation = context.TargetContext.ActivationServices.GetService<DurableJobTurnIsolation>();
        if (isolation is null || !isolation.IsEnabled)
        {
            await context.Invoke();
            return;
        }

        using var lease = await isolation.EnterOrdinaryAsync();
        lease.Activate();
        await context.Invoke();
    }
}

internal sealed class DurableJobExecutionLifetime : ILifecycleObserver
{
    private readonly CancellationTokenSource _shutdown = new();

    public DurableJobExecutionLifetime(IGrainContext grainContext)
    {
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableJobExecutionLifetime),
            GrainLifecycleStage.Last,
            this);
    }

    public CancellationToken Token => _shutdown.Token;

    public Task OnStart(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnStop(CancellationToken cancellationToken = default)
    {
        _shutdown.Cancel();
        return Task.CompletedTask;
    }
}
