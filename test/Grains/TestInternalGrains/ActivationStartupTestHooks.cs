using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace UnitTests.Grains;

public enum ActivationStartupCompletion
{
    ImmediateSuccess,
    AsynchronousSuccess,
    ImmediateFailure,
    AsynchronousFailure,
    Cancellation,
}

public enum ActivationStartupDisposal
{
    Synchronous,
    Asynchronous,
}

public readonly record struct ActivationStartupEvent(string Name, GrainId GrainId, ActivationId ActivationId);

public sealed class ActivationStartupTestHooks
{
    public const string RequestContextKey = "activation-startup";

    private readonly AsyncLocal<string?> _ambientValue = new();
    private readonly ConcurrentDictionary<GrainId, ActivationStartupScenario> _scenarios = new();

    public string? AmbientValue
    {
        get => _ambientValue.Value;
        set => _ambientValue.Value = value;
    }

    public ActivationStartupScenario CreateScenario(
        GrainId grainId,
        ActivationStartupCompletion completion,
        ActivationStartupDisposal disposal)
    {
        var scenario = new ActivationStartupScenario(this, grainId, completion, disposal);
        if (!_scenarios.TryAdd(grainId, scenario))
        {
            throw new InvalidOperationException($"A startup scenario already exists for grain '{grainId}'.");
        }

        return scenario;
    }

    public ActivationStartupScenario GetRequiredScenario(GrainId grainId) =>
        _scenarios.TryGetValue(grainId, out var scenario)
            ? scenario
            : throw new InvalidOperationException($"No startup scenario exists for grain '{grainId}'.");

    public bool TryGetScenario(GrainId grainId, out ActivationStartupScenario? scenario) =>
        _scenarios.TryGetValue(grainId, out scenario);

    public void RemoveScenario(GrainId grainId)
    {
        _scenarios.TryRemove(grainId, out _);
    }
}

public sealed class ActivationStartupScenario
{
    private readonly ActivationStartupTestHooks _hooks;
    private readonly TaskCompletionSource _activationRelease = CreateCompletionSource();
    private readonly TaskCompletionSource _disposalRelease = CreateCompletionSource();
    private readonly ConcurrentQueue<ActivationStartupEvent> _events = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ActivationStartupEvent>> _eventWaiters = new();
    private IGrainContext? _context;
    private string? _constructorAmbientValue;
    private string? _constructorRequestContextValue;
    private string? _onActivateAmbientValue;
    private string? _onActivateRequestContextValue;
    private string? _requestAmbientValue;
    private string? _requestContextValue;
    private int _constructorCount;
    private int _createCount;
    private int _disposeCompletedCount;
    private int _disposeStartedCount;
    private int _onActivateCount;
    private int _onDeactivateCount;
    private int _requestInvocationCount;
    private int _scopeDisposeCount;

    internal ActivationStartupScenario(
        ActivationStartupTestHooks hooks,
        GrainId grainId,
        ActivationStartupCompletion completion,
        ActivationStartupDisposal disposal)
    {
        _hooks = hooks;
        GrainId = grainId;
        Completion = completion;
        Disposal = disposal;
        ActivationException = new InvalidOperationException("activate-fault");
    }

    public GrainId GrainId { get; }

    public ActivationStartupCompletion Completion { get; }

    public ActivationStartupDisposal Disposal { get; }

    public InvalidOperationException ActivationException { get; }

    public IReadOnlyList<ActivationStartupEvent> Events => _events.ToArray();

    public int ConstructorCount => Volatile.Read(ref _constructorCount);

    public int CreateCount => Volatile.Read(ref _createCount);

    public int DisposeCompletedCount => Volatile.Read(ref _disposeCompletedCount);

    public int DisposeStartedCount => Volatile.Read(ref _disposeStartedCount);

    public int OnActivateCount => Volatile.Read(ref _onActivateCount);

    public int OnDeactivateCount => Volatile.Read(ref _onDeactivateCount);

    public int RequestInvocationCount => Volatile.Read(ref _requestInvocationCount);

    public int ScopeDisposeCount => Volatile.Read(ref _scopeDisposeCount);

    public string? ConstructorAmbientValue => Volatile.Read(ref _constructorAmbientValue);

    public string? ConstructorRequestContextValue => Volatile.Read(ref _constructorRequestContextValue);

    public string? OnActivateAmbientValue => Volatile.Read(ref _onActivateAmbientValue);

    public string? OnActivateRequestContextValue => Volatile.Read(ref _onActivateRequestContextValue);

    public string? RequestAmbientValue => Volatile.Read(ref _requestAmbientValue);

    public string? RequestContextValue => Volatile.Read(ref _requestContextValue);

    internal IGrainContext Context =>
        Volatile.Read(ref _context)
        ?? throw new InvalidOperationException($"The startup scenario for grain '{GrainId}' has no activation context.");

    internal Task ActivationRelease => _activationRelease.Task;

    internal Task DisposalRelease => _disposalRelease.Task;

    public Task<ActivationStartupEvent> WaitForEvent(string name) =>
        _eventWaiters.GetOrAdd(name, static _ => CreateEventCompletionSource()).Task;

    public void ReleaseActivation() => _activationRelease.TrySetResult();

    public void ReleaseDisposal() => _disposalRelease.TrySetResult();

    public int GetEventCount(string name) => _events.Count(entry => entry.Name == name);

    public void Record(string name, IGrainContext context)
    {
        Attach(context);
        var entry = new ActivationStartupEvent(name, context.GrainId, context.ActivationId);
        _events.Enqueue(entry);
        _eventWaiters.GetOrAdd(name, static _ => CreateEventCompletionSource()).TrySetResult(entry);
    }

    internal void ObserveCreate(IGrainContext context)
    {
        Attach(context);
        Interlocked.Increment(ref _createCount);
    }

    internal void ObserveConstructor(IGrainContext context)
    {
        Attach(context);
        Interlocked.Increment(ref _constructorCount);
        Volatile.Write(ref _constructorAmbientValue, _hooks.AmbientValue);
        Volatile.Write(
            ref _constructorRequestContextValue,
            RequestContext.Get(ActivationStartupTestHooks.RequestContextKey) as string);
    }

    internal void ObserveOnActivate(IGrainContext context)
    {
        Interlocked.Increment(ref _onActivateCount);
        Volatile.Write(ref _onActivateAmbientValue, _hooks.AmbientValue);
        Volatile.Write(
            ref _onActivateRequestContextValue,
            RequestContext.Get(ActivationStartupTestHooks.RequestContextKey) as string);
        Record("OnActivateEntered", context);
    }

    internal void ObserveOnDeactivate(IGrainContext context)
    {
        Interlocked.Increment(ref _onDeactivateCount);
        Record("OnDeactivateEntered", context);
    }

    internal void ObserveRequest(IGrainContext context)
    {
        Interlocked.Increment(ref _requestInvocationCount);
        Volatile.Write(ref _requestAmbientValue, _hooks.AmbientValue);
        Volatile.Write(
            ref _requestContextValue,
            RequestContext.Get(ActivationStartupTestHooks.RequestContextKey) as string);
        Record("RequestInvoked", context);
    }

    internal void ObserveDisposeStarted(IGrainContext context)
    {
        Interlocked.Increment(ref _disposeStartedCount);
        Record("ActivatorDisposeStarted", context);
    }

    internal void ObserveDisposeCompleted(IGrainContext context)
    {
        Interlocked.Increment(ref _disposeCompletedCount);
        Record("ActivatorDisposeCompleted", context);
    }

    internal void ObserveScopeDisposed(IGrainContext context)
    {
        Interlocked.Increment(ref _scopeDisposeCount);
        Record("ScopeDisposed", context);
    }

    private void Attach(IGrainContext context)
    {
        if (!context.GrainId.Equals(GrainId))
        {
            throw new InvalidOperationException(
                $"Scenario grain '{GrainId}' cannot observe activation '{context.GrainId}'.");
        }

        var existing = Interlocked.CompareExchange(ref _context, context, null);
        if (existing is not null && !ReferenceEquals(existing, context))
        {
            throw new InvalidOperationException($"Scenario grain '{GrainId}' observed multiple activation contexts.");
        }
    }

    private static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<ActivationStartupEvent> CreateEventCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ActivationStartupScopedResource(ActivationStartupTestHooks hooks) : IDisposable
{
    private IGrainContext? _context;
    private int _disposed;

    public void Attach(IGrainContext context) => _context = context;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0
            && _context is { } context
            && hooks.TryGetScenario(context.GrainId, out var scenario))
        {
            scenario!.ObserveScopeDisposed(context);
        }
    }
}

public enum CatalogActivationStartupOutcome
{
    Success,
    ConstructorFailure,
}

public sealed class CatalogActivationStartupTestHooks
{
    private readonly ConcurrentDictionary<GrainId, CatalogActivationStartupGate> _gates = new();

    public CatalogActivationStartupGate CreateGate(
        GrainId grainId,
        CatalogActivationStartupOutcome outcome)
    {
        var gate = new CatalogActivationStartupGate(grainId, outcome);
        if (!_gates.TryAdd(grainId, gate))
        {
            throw new InvalidOperationException($"A catalog startup gate already exists for grain '{grainId}'.");
        }

        return gate;
    }

    public bool TryGetGate(GrainId grainId, out CatalogActivationStartupGate? gate) =>
        _gates.TryGetValue(grainId, out gate);

    public void RemoveGate(GrainId grainId) => _gates.TryRemove(grainId, out _);
}

public sealed class CatalogActivationStartupGate(
    GrainId grainId,
    CatalogActivationStartupOutcome outcome)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly TaskCompletionSource<IGrainContext> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entryCount;

    public GrainId GrainId { get; } = grainId;

    public CatalogActivationStartupOutcome Outcome { get; } = outcome;

    public InvalidOperationException ConstructorException { get; } =
        new("constructor-fault");

    public Task<IGrainContext> Entered => _entered.Task;

    public int EntryCount => Volatile.Read(ref _entryCount);

    public void Release() => _release.TrySetResult();

    internal void EnterAndWait(IGrainContext context, ActivationStartupScenario scenario)
    {
        if (!context.GrainId.Equals(GrainId))
        {
            throw new InvalidOperationException(
                $"Catalog startup gate for grain '{GrainId}' cannot observe activation '{context.GrainId}'.");
        }

        if (Interlocked.Increment(ref _entryCount) != 1)
        {
            throw new InvalidOperationException(
                $"Catalog startup gate for grain '{GrainId}' was entered more than once.");
        }

        scenario.Record("ConstructorBlocked", context);
        _entered.TrySetResult(context);
        try
        {
            _release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out releasing catalog startup for grain '{context.GrainId}', activation '{context.ActivationId}', outcome '{Outcome}'.",
                exception);
        }
    }
}

public sealed class CatalogActivationStartupTestActivator(
    ActivationStartupTestHooks hooks,
    CatalogActivationStartupTestHooks catalogHooks) : IGrainActivator
{
    public object CreateInstance(IGrainContext context)
    {
        var scenario = hooks.GetRequiredScenario(context.GrainId);
        scenario.ObserveCreate(context);
        context.ActivationServices.GetRequiredService<ActivationStartupScopedResource>().Attach(context);
        var instance = new ActivationStartupTestGrain(context, hooks);

        if (catalogHooks.TryGetGate(context.GrainId, out var gate))
        {
            gate!.EnterAndWait(context, scenario);
            if (gate.Outcome is CatalogActivationStartupOutcome.ConstructorFailure)
            {
                scenario.Record("ConstructorFailed", context);
                throw gate.ConstructorException;
            }
        }

        return instance;
    }

    public async ValueTask DisposeInstance(IGrainContext context, object instance)
    {
        var scenario = hooks.GetRequiredScenario(context.GrainId);
        scenario.ObserveDisposeStarted(context);
        if (scenario.Disposal is ActivationStartupDisposal.Asynchronous)
        {
            await scenario.DisposalRelease;
        }

        scenario.ObserveDisposeCompleted(context);
    }
}
