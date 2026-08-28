using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableJobs;

internal interface IDurableJobTurnIsolationReentrantScope
{
    IDisposable JoinReentrantScope();
}

internal sealed class DurableJobTurnIsolation : IDurableTaskTurnIsolation
{
    internal const string RequestContextKey = "Orleans.DurableJobs.TurnIsolation";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _activationKey = Guid.NewGuid().ToString("N");
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
                return new Lease(this, _ownerId!, RequestContext.Get(RequestContextKey), isReentrant: true);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        var ownerId = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _ownerId = ownerId;
            _leaseCount = 1;
        }

        return new Lease(this, ownerId, RequestContext.Get(RequestContextKey), isReentrant: false);
    }

    public async ValueTask<Lease> EnterOrdinaryAsync(CancellationToken cancellationToken = default)
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
                return new Lease(this, _ownerId!, RequestContext.Get(RequestContextKey), isReentrant: true);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        var ownerId = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _ownerId = ownerId;
            _leaseCount = 1;
        }

        return new Lease(this, ownerId, RequestContext.Get(RequestContextKey), isReentrant: false);
    }

    private bool IsCurrentOwnerUnderLock() =>
        _leaseCount > 0
        && _ownerId is { } ownerId
        && RequestContext.Get(RequestContextKey) is IReadOnlyDictionary<string, string> owners
        && owners.TryGetValue(_activationKey, out var currentOwner)
        && string.Equals(currentOwner, ownerId, StringComparison.Ordinal);

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

    async ValueTask<IDurableTaskTurnIsolationLease> IDurableTaskTurnIsolation.EnterAsync(
        CancellationToken cancellationToken) =>
        await EnterOrdinaryAsync(cancellationToken);

    public sealed class Lease : IDurableTaskTurnIsolationLease
    {
        public static Lease None { get; } = new(null, null, null, isReentrant: false);

        private readonly DurableJobTurnIsolation? _owner;
        private readonly string? _ownerId;
        private readonly object? _previousOwner;
        private bool _activated;
        private int _disposed;

        internal Lease(
            DurableJobTurnIsolation? owner,
            string? ownerId,
            object? previousOwner,
            bool isReentrant)
        {
            _owner = owner;
            _ownerId = ownerId;
            _previousOwner = previousOwner;
            IsReentrant = isReentrant;
        }

        public bool IsReentrant { get; }

        public void Activate()
        {
            if (_owner is not null && !_activated)
            {
                var owners = _previousOwner is IReadOnlyDictionary<string, string> previous
                    ? new Dictionary<string, string>(previous, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                owners[_owner._activationKey] = _ownerId!;
                RequestContext.Set(RequestContextKey, owners);
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
            || context.InterfaceMethod.DeclaringType == typeof(IDurableJobFeatureReceiverExtension)
            || context.InterfaceMethod.DeclaringType == typeof(IDurableJobReceiverExtension))
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
        List<IDisposable>? reentrantScopes = null;
        if (lease.IsReentrant)
        {
            foreach (var participant in context.TargetContext.ActivationServices.GetServices<IDurableJobTurnIsolationReentrantScope>())
            {
                (reentrantScopes ??= []).Add(participant.JoinReentrantScope());
            }
        }

        try
        {
            await context.Invoke();
        }
        finally
        {
            if (reentrantScopes is not null)
            {
                for (var i = reentrantScopes.Count - 1; i >= 0; i--)
                {
                    reentrantScopes[i].Dispose();
                }
            }
        }
    }
}

internal sealed class DurableJobExecutionLifetime : ILifecycleObserver
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _executions = [];
    private bool _admissionStopped;

    public DurableJobExecutionLifetime(IGrainContext grainContext)
        : this()
    {
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableJobExecutionLifetime),
            GrainLifecycleStage.Last,
            this);
    }

    internal DurableJobExecutionLifetime()
    {
    }

    public CancellationToken Token => _shutdown.Token;

    public Task<TResult> Start<TResult>(Func<CancellationToken, Task<TResult>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Task<TResult> task;
        lock (_sync)
        {
            if (_admissionStopped)
            {
                throw new OperationCanceledException("The durable job activation is stopping.");
            }

            task = factory(_shutdown.Token);
            _executions.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (DurableJobExecutionLifetime)state!;
                lock (owner._sync)
                {
                    owner._executions.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    public Task OnStart(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task OnStop(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _admissionStopped = true;
        }

        _shutdown.Cancel();
        Task[] executions;
        lock (_sync)
        {
            executions = _executions.ToArray();
        }

        await Task.WhenAll(executions).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}

internal sealed class DurableJobGrainContextConfigurator :
    IConfigureGrainContextProvider,
    IConfigureGrainContextPerActivation
{
    public bool TryGetConfigurator(
        GrainType grainType,
        GrainProperties properties,
        [NotNullWhen(true)] out IConfigureGrainContext? configurator)
    {
        configurator = this;
        return true;
    }

    public void Configure(IGrainContext context)
    {
        if (context is StatelessWorkerGrainContext)
        {
            return;
        }

        context.SetComponent(new DurableJobExecutionLifetime(context));
    }
}
