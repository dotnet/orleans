#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Shared, hand-written test fakes reused across the Orleans.DurableTasks.Tests project.
/// Mirrors the pattern used in test/Orleans.Journaling.Tests/JournalBatchTests.cs (TestGrainContext).
/// </summary>
internal sealed class TestGrainContext(GrainId grainId) : IGrainContext
{
    public GrainReference GrainReference => throw new NotImplementedException();
    public GrainId GrainId => grainId;
    public object? GrainInstance => throw new NotImplementedException();
    public ActivationId ActivationId => throw new NotImplementedException();
    public GrainAddress Address => throw new NotImplementedException();
    public IServiceProvider ActivationServices => throw new NotImplementedException();
    public IGrainLifecycle ObservableLifecycle { get; } = new TestGrainLifecycle();
    public IWorkItemScheduler Scheduler => throw new NotImplementedException();
    public Task Deactivated => throw new NotImplementedException();

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public bool Equals(IGrainContext? other) => throw new NotImplementedException();
    public TComponent? GetComponent<TComponent>() where TComponent : class => throw new NotImplementedException();
    public object? GetComponent(Type componentType) => throw new NotImplementedException();
    public TTarget? GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();
    public object? GetTarget() => throw new NotImplementedException();
    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void ReceiveMessage(object message) => throw new NotImplementedException();
    public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
    public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotImplementedException();
}

/// <summary>
/// Minimal fake <see cref="IGrainLifecycle"/> that records subscriptions instead of invoking them.
/// Sufficient for testing components (e.g. lifecycle participants) that only call <c>Subscribe</c> during initialization.
/// </summary>
internal sealed class TestGrainLifecycle : IGrainLifecycle
{
    public List<(string ObserverName, int Stage)> Subscriptions { get; } = [];

    public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
    {
        Subscriptions.Add((observerName, stage));
        return new NoopDisposable();
    }

    public void AddMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotImplementedException();
    public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotImplementedException();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Trivial <see cref="IGrainContextAccessor"/> fake that always returns the supplied context.
/// </summary>
internal sealed class TestGrainContextAccessor(IGrainContext context) : IGrainContextAccessor
{
    public IGrainContext GrainContext { get; } = context;
}
