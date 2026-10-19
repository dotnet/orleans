#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Shared, hand-written test fakes reused across the Microsoft.Orleans.DurableTasks.Tests project.
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

/// <summary>
/// Runs the local requests which a substitute grain context receives on the scheduler which queued them: one at a time, in
/// arrival order, as a non-reentrant activation does, or as soon as they arrive, as a reentrant one does.
/// </summary>
internal sealed class TestLocalRequestQueue(bool reentrant)
{
    private readonly Queue<(ILocalRequest Request, TaskScheduler Scheduler)> _queue = new();
    private readonly object _lock = new();
    private bool _running;

    /// <summary>Gets the number of requests which ran.</summary>
    public int Completed { get; private set; }

    /// <summary>Makes <paramref name="context"/>, a substitute, run its local requests through a new queue.</summary>
    public static TestLocalRequestQueue Attach(IGrainContext context, bool reentrant = false)
    {
        var queue = new TestLocalRequestQueue(reentrant);
        context
            .When(static grainContext => grainContext.ReceiveLocalRequest(Arg.Any<ILocalRequest>(), Arg.Any<LocalRequestOptions>()))
            .Do(call => queue.Enqueue(call.Arg<ILocalRequest>()));
        return queue;
    }

    private void Enqueue(ILocalRequest request)
    {
        if (reentrant)
        {
            Run(request, TaskScheduler.Current, runNext: false);
            return;
        }

        lock (_lock)
        {
            _queue.Enqueue((request, TaskScheduler.Current));
            if (_running)
            {
                return;
            }

            _running = true;
        }

        RunNext();
    }

    private void RunNext()
    {
        (ILocalRequest Request, TaskScheduler Scheduler) next;
        lock (_lock)
        {
            if (!_queue.TryDequeue(out next))
            {
                _running = false;
                return;
            }
        }

        Run(next.Request, next.Scheduler, runNext: true);
    }

    private void Run(ILocalRequest request, TaskScheduler scheduler, bool runNext)
    {
        _ = Task.Factory.StartNew(
            async () =>
            {
                Response response;
                try
                {
                    response = await request.Invoke();
                }
                catch (Exception exception)
                {
                    response = Response.FromException(exception);
                }

                request.Complete(response);
                request.Dispose();
                Completed++;
                if (runNext)
                {
                    RunNext();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).Unwrap();
    }
}

/// <summary>Runs runtime operations which an attempt calls, from a test which drives the runtime directly.</summary>
internal static class DurableTaskGrainRuntimeTestExtensions
{
    /// <summary>Selects a completion as an attempt of the execution which owns <paramref name="decisionId"/> does.</summary>
    public static ValueTask<TaskId> SelectCompletionAsync(
        this global::Orleans.DurableTasks.Runtime.DurableTaskGrainRuntime runtime,
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) =>
        runtime.SelectCompletionAsync(
            new global::Orleans.DurableTasks.Runtime.GrainDurableTaskContext(decisionId.Parent(), runtime, runtime.GrainContext, TaskScheduler.Current),
            decisionId,
            candidates,
            cancellationToken);
}
