using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class StatelessWorkerActivationStartupTests(
    StatelessWorkerActivationStartupTests.Fixture fixture)
    : IClassFixture<StatelessWorkerActivationStartupTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly FieldInfo WorkersField =
        typeof(StatelessWorkerGrainContext).GetField(
            "_workers",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The stateless-worker child list was not found.");

    [Fact]
    public async Task StatelessWorkerChildIsPublishedBeforeSynchronousStartAndDoesNotInvokeEarly()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.ImmediateSuccess,
            ActivationStartupDisposal.Synchronous);
        var gate = fixture.CreateConstructorGate(grainId, CatalogActivationStartupOutcome.Success);
        using var eventSubscription = fixture.ObserveEvents();
        using var collector = new DiagnosticEventCollector(StatelessWorkerEvents.ListenerName);
        var wrapper = fixture.GetOrCreateContext(grainId);
        var workerCreatedTask = WaitForWorkerCreatedAsync(collector, wrapper);
        var responseTask = scenario.WaitForEvent("Response");
        ActivationData? worker = null;
        Task? admissionTask = null;

        try
        {
            var (message, request) = fixture.CreateRequest(
                wrapper,
                scenario,
                payload: "synchronous",
                requestContextValue: "sync-request");

            admissionTask = Task.Run(() => wrapper.ReceiveMessage(message));
            worker = Assert.IsType<ActivationData>(
                await gate.Entered.WaitAsync(Timeout, TestContext.Current.CancellationToken));

            Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
            Assert.Equal(1, gate.EntryCount);
            Assert.Equal(1, scenario.CreateCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(0, scenario.OnActivateCount);
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Null(request.Result);
            Assert.Empty(GetWorkerCreatedEvents(collector, wrapper));

            gate.Release();
            await admissionTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var workerCreated = await workerCreatedTask;
            await responseTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Same(wrapper, workerCreated.Context);
            Assert.Same(worker, workerCreated.WorkerContext);
            Assert.Equal(1, workerCreated.WorkerCount);
            Assert.Equal(
                $"synchronous:{worker.ActivationId}:sync-request",
                request.Result);
            Assert.Equal(1, scenario.CreateCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(1, scenario.RequestInvocationCount);
            Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
            AssertScenarioEventOrder(
                scenario,
                "ConstructorEntered",
                "ConstructorBlocked",
                "WorkerCreated",
                "LifecycleStartLow",
                "LifecycleStartHigh",
                "OnActivateEntered",
                "OnActivateCompleted",
                "RequestInvoked",
                "Response");
            AssertIdentity(scenario, grainId, worker.ActivationId);
            Assert.Empty(GetContextTerminatedEvents(collector, wrapper));
            Assert.Empty(GetMessageForwardedEvents(collector, grainId));
        }
        finally
        {
            gate.Release();
            if (admissionTask is not null)
            {
                await admissionTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            await CleanupAsync(wrapper, worker, scenario, collector);
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task StatelessWorkerRequestWaitsForAsyncStartupBeforeInvocation()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Synchronous);
        using var eventSubscription = fixture.ObserveEvents();
        using var collector = new DiagnosticEventCollector(StatelessWorkerEvents.ListenerName);
        var wrapper = fixture.GetOrCreateContext(grainId);
        var workerCreatedTask = WaitForWorkerCreatedAsync(collector, wrapper);
        var activationEnteredTask = scenario.WaitForEvent("OnActivateEntered");
        var responseTask = scenario.WaitForEvent("Response");
        ActivationData? worker = null;

        try
        {
            var (message, request) = fixture.CreateRequest(
                wrapper,
                scenario,
                payload: "asynchronous",
                requestContextValue: "async-request");

            wrapper.ReceiveMessage(message);
            var workerCreated = await workerCreatedTask;
            worker = Assert.IsType<ActivationData>(workerCreated.WorkerContext);
            await activationEnteredTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
            Assert.Equal(1, workerCreated.WorkerCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Null(request.Result);

            scenario.ReleaseActivation();
            await responseTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(
                $"asynchronous:{worker.ActivationId}:async-request",
                request.Result);
            Assert.Equal(1, scenario.RequestInvocationCount);
            Assert.Equal("async-request", scenario.RequestContextValue);
            AssertScenarioEventOrder(
                scenario,
                "WorkerCreated",
                "OnActivateEntered",
                "OnActivateCompleted",
                "RequestInvoked",
                "Response");
            AssertIdentity(scenario, grainId, worker.ActivationId);
            Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
            Assert.Single(GetWorkerCreatedEvents(collector, wrapper));
            Assert.Empty(GetContextTerminatedEvents(collector, wrapper));
            Assert.Empty(GetMessageForwardedEvents(collector, grainId));
        }
        finally
        {
            await CleanupAsync(wrapper, worker, scenario, collector);
            fixture.RemoveScenario(grainId);
        }
    }

    [Theory]
    [InlineData(StatelessWorkerStartupFailure.Constructor)]
    [InlineData(StatelessWorkerStartupFailure.AsynchronousActivation)]
    public async Task StatelessWorkerStartupFailureRemovesChildAndDisposesResourcesExactlyOnce(
        StatelessWorkerStartupFailure failure)
    {
        var completion = failure is StatelessWorkerStartupFailure.Constructor
            ? ActivationStartupCompletion.ImmediateSuccess
            : ActivationStartupCompletion.AsynchronousFailure;
        var disposal = failure is StatelessWorkerStartupFailure.Constructor
            ? ActivationStartupDisposal.Synchronous
            : ActivationStartupDisposal.Asynchronous;
        var (grainId, scenario) = fixture.CreateScenario(completion, disposal);
        var gate = failure is StatelessWorkerStartupFailure.Constructor
            ? fixture.CreateConstructorGate(grainId, CatalogActivationStartupOutcome.ConstructorFailure)
            : null;
        using var eventSubscription = fixture.ObserveEvents();
        using var statelessEvents = new DiagnosticEventCollector(StatelessWorkerEvents.ListenerName);
        using var dispatcherEvents = new DiagnosticEventCollector(DispatcherEvents.ListenerName);
        var wrapper = fixture.GetOrCreateContext(grainId);
        var workerCreatedTask = WaitForWorkerCreatedAsync(statelessEvents, wrapper);
        var contextTerminatedTask = WaitForContextTerminatedAsync(statelessEvents, wrapper);
        var deactivatedTask = scenario.WaitForEvent("Deactivated");
        var scopeDisposedTask = scenario.WaitForEvent("ScopeDisposed");
        ActivationData? worker = null;
        Task? admissionTask = null;

        try
        {
            var (message, request) = fixture.CreateRequest(
                wrapper,
                scenario,
                payload: "must-not-run",
                requestContextValue: $"failure-{failure}");
            var rejectedTask = WaitForRejectionAsync(dispatcherEvents, message);

            if (gate is not null)
            {
                admissionTask = Task.Run(() => wrapper.ReceiveMessage(message));
                worker = Assert.IsType<ActivationData>(
                    await gate.Entered.WaitAsync(Timeout, TestContext.Current.CancellationToken));
                Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
                Assert.Equal(0, scenario.RequestInvocationCount);
                gate.Release();
                await admissionTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }
            else
            {
                wrapper.ReceiveMessage(message);
                await scenario.WaitForEvent("OnActivateEntered")
                    .WaitAsync(Timeout, TestContext.Current.CancellationToken);
                worker = Assert.IsType<ActivationData>((await workerCreatedTask).WorkerContext);
                Assert.Same(worker, Assert.Single(GetWorkers(wrapper)));
                Assert.Equal(0, scenario.RequestInvocationCount);
                scenario.ReleaseActivation();
                await scenario.WaitForEvent("OnActivateFailed")
                    .WaitAsync(Timeout, TestContext.Current.CancellationToken);
                await scenario.WaitForEvent("ActivatorDisposeStarted")
                    .WaitAsync(Timeout, TestContext.Current.CancellationToken);
                Assert.Equal(0, scenario.DisposeCompletedCount);
                Assert.Equal(0, scenario.ScopeDisposeCount);
                scenario.ReleaseDisposal();
            }

            var workerCreated = await workerCreatedTask;
            worker ??= Assert.IsType<ActivationData>(workerCreated.WorkerContext);
            var rejectionEvent = await rejectedTask;
            var terminated = await contextTerminatedTask;
            await Task.WhenAll(deactivatedTask, scopeDisposedTask)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken);

            var rejection = Assert.IsType<DispatcherEvents.Rejected>(rejectionEvent.Payload);
            var expectedException = gate?.ConstructorException ?? scenario.ActivationException;
            var expectedExceptionMessage = failure is StatelessWorkerStartupFailure.Constructor
                ? "constructor-fault"
                : "activate-fault";
            Assert.Same(expectedException, rejection.Exception);
            Assert.Equal(expectedExceptionMessage, rejection.Exception!.Message);
            Assert.Equal(Message.RejectionTypes.Transient, rejection.RejectionType);
            Assert.Equal(
                failure is StatelessWorkerStartupFailure.Constructor
                    ? "Error constructing grain instance."
                    : "Failed to activate grain.",
                rejection.Reason);
            Assert.Same(message, rejection.Message);
            Assert.Null(request.Result);
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Equal(0, scenario.GetEventCount("Activated"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Equal(
                failure is StatelessWorkerStartupFailure.Constructor ? 0 : 1,
                scenario.DisposeStartedCount);
            Assert.Equal(
                failure is StatelessWorkerStartupFailure.Constructor ? 0 : 1,
                scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Equal(0, terminated.WorkerCount);
            Assert.Empty(GetWorkers(wrapper));
            Assert.Single(GetWorkerCreatedEvents(statelessEvents, wrapper));
            Assert.Single(GetContextTerminatedEvents(statelessEvents, wrapper));
            Assert.Empty(GetMessageForwardedEvents(statelessEvents, grainId));
            if (failure is StatelessWorkerStartupFailure.Constructor)
            {
                AssertScenarioEventOrder(
                    scenario,
                    "ConstructorEntered",
                    "ConstructorBlocked",
                    "ConstructorFailed",
                    "Deactivating",
                    "WorkerCreated",
                    "ScopeDisposed",
                    "Deactivated");
            }
            else
            {
                AssertScenarioEventOrder(
                    scenario,
                    "ConstructorEntered",
                    "WorkerCreated",
                    "OnActivateEntered",
                    "OnActivateFailed",
                    "Deactivating",
                    "ActivatorDisposeStarted",
                    "ActivatorDisposeCompleted",
                    "ScopeDisposed",
                    "Deactivated");
            }

            AssertIdentity(scenario, grainId, worker.ActivationId);

            var eventSnapshot = scenario.Events.ToArray();
            await worker.DisposeAsync();
            await worker.DisposeAsync();
            Assert.Equal(eventSnapshot, scenario.Events);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Equal(
                failure is StatelessWorkerStartupFailure.Constructor ? 0 : 1,
                scenario.DisposeCompletedCount);
            Assert.Single(GetContextTerminatedEvents(statelessEvents, wrapper));
        }
        finally
        {
            gate?.Release();
            if (admissionTask is not null)
            {
                await admissionTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            await CleanupAsync(wrapper, worker, scenario, statelessEvents);
            fixture.RemoveScenario(grainId);
        }
    }

    private static IReadOnlyList<ActivationData> GetWorkers(StatelessWorkerGrainContext context) =>
        [.. Assert.IsType<List<ActivationData>>(WorkersField.GetValue(context))];

    private static IReadOnlyList<StatelessWorkerEvents.WorkerCreated> GetWorkerCreatedEvents(
        DiagnosticEventCollector collector,
        IGrainContext context) =>
        [.. collector.Events
            .Select(static evt => evt.Payload)
            .OfType<StatelessWorkerEvents.WorkerCreated>()
            .Where(evt => ReferenceEquals(evt.Context, context))];

    private static IReadOnlyList<StatelessWorkerEvents.ContextTerminated> GetContextTerminatedEvents(
        DiagnosticEventCollector collector,
        IGrainContext context) =>
        [.. collector.Events
            .Select(static evt => evt.Payload)
            .OfType<StatelessWorkerEvents.ContextTerminated>()
            .Where(evt => ReferenceEquals(evt.Context, context))];

    private static IReadOnlyList<StatelessWorkerEvents.MessageForwarded> GetMessageForwardedEvents(
        DiagnosticEventCollector collector,
        GrainId grainId) =>
        [.. collector.Events
            .Select(static evt => evt.Payload)
            .OfType<StatelessWorkerEvents.MessageForwarded>()
            .Where(evt => evt.GrainId.Equals(grainId))];

    private static async Task<StatelessWorkerEvents.WorkerCreated> WaitForWorkerCreatedAsync(
        DiagnosticEventCollector collector,
        IGrainContext context)
    {
        var evt = await collector.WaitForEventAsync(
            nameof(StatelessWorkerEvents.WorkerCreated),
            diagnosticEvent => diagnosticEvent.Payload is StatelessWorkerEvents.WorkerCreated created
                && ReferenceEquals(created.Context, context),
            Timeout,
            TestContext.Current.CancellationToken);
        return Assert.IsType<StatelessWorkerEvents.WorkerCreated>(evt.Payload);
    }

    private static async Task<StatelessWorkerEvents.ContextTerminated> WaitForContextTerminatedAsync(
        DiagnosticEventCollector collector,
        IGrainContext context)
    {
        var evt = await collector.WaitForEventAsync(
            nameof(StatelessWorkerEvents.ContextTerminated),
            diagnosticEvent => diagnosticEvent.Payload is StatelessWorkerEvents.ContextTerminated terminated
                && ReferenceEquals(terminated.Context, context),
            Timeout,
            TestContext.Current.CancellationToken);
        return Assert.IsType<StatelessWorkerEvents.ContextTerminated>(evt.Payload);
    }

    private static Task<DiagnosticEvent> WaitForRejectionAsync(
        DiagnosticEventCollector collector,
        Message message) =>
        collector.WaitForEventAsync(
            nameof(DispatcherEvents.Rejected),
            diagnosticEvent => diagnosticEvent.Payload is DispatcherEvents.Rejected rejected
                && ReferenceEquals(rejected.Message, message),
            Timeout,
            TestContext.Current.CancellationToken);

    private static async Task CleanupAsync(
        StatelessWorkerGrainContext wrapper,
        ActivationData? worker,
        ActivationStartupScenario scenario,
        DiagnosticEventCollector collector)
    {
        scenario.ReleaseActivation();
        scenario.ReleaseDisposal();
        if (worker is not null && scenario.GetEventCount("Deactivated") == 0)
        {
            var deactivated = scenario.WaitForEvent("Deactivated");
            worker.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Test cleanup."));
            await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        if (worker is not null)
        {
            await WaitForContextTerminatedAsync(collector, wrapper);
        }

        await wrapper.DisposeAsync();
    }

    private static void AssertScenarioEventOrder(
        ActivationStartupScenario scenario,
        params string[] expected)
    {
        var expectedSet = expected.ToHashSet();
        Assert.Equal(
            expected,
            scenario.Events
                .Where(entry => expectedSet.Contains(entry.Name))
                .Select(static entry => entry.Name));
        Assert.All(expected, name => Assert.Equal(1, scenario.GetEventCount(name)));
    }

    private static void AssertIdentity(
        ActivationStartupScenario scenario,
        GrainId grainId,
        ActivationId activationId)
    {
        Assert.NotEmpty(scenario.Events);
        Assert.All(
            scenario.Events,
            entry =>
            {
                Assert.Equal(grainId, entry.GrainId);
                Assert.Equal(activationId, entry.ActivationId);
            });
    }

    public enum StatelessWorkerStartupFailure
    {
        Constructor,
        AsynchronousActivation,
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        internal IServiceProvider Services =>
            ((InProcessSiloHandle)HostedCluster.Primary!).SiloHost.Services;

        internal ActivationDirectory ActivationDirectory =>
            Services.GetRequiredService<ActivationDirectory>();

        private ActivationStartupTestHooks Hooks =>
            Services.GetRequiredService<ActivationStartupTestHooks>();

        private CatalogActivationStartupTestHooks CatalogHooks =>
            Services.GetRequiredService<CatalogActivationStartupTestHooks>();

        internal (GrainId GrainId, ActivationStartupScenario Scenario) CreateScenario(
            ActivationStartupCompletion completion,
            ActivationStartupDisposal disposal)
        {
            var grainType = Services.GetRequiredService<GrainTypeResolver>()
                .GetGrainType(typeof(StatelessWorkerActivationStartupTestGrain));
            var grainId = GrainId.Create(grainType, Guid.NewGuid().ToString("N"));
            return (grainId, Hooks.CreateScenario(grainId, completion, disposal));
        }

        internal ActivationStartupScenario CreateScenario(
            GrainId grainId,
            ActivationStartupCompletion completion,
            ActivationStartupDisposal disposal) =>
            Hooks.CreateScenario(grainId, completion, disposal);

        internal CatalogActivationStartupGate CreateConstructorGate(
            GrainId grainId,
            CatalogActivationStartupOutcome outcome) =>
            CatalogHooks.CreateGate(grainId, outcome);

        internal StatelessWorkerGrainContext GetOrCreateContext(GrainId grainId) =>
            Assert.IsType<StatelessWorkerGrainContext>(
                Services.GetRequiredService<Catalog>()
                    .GetOrCreateActivation(
                        grainId,
                        requestContextData: null,
                        rehydrationContext: null));

        internal (Message Message, StatelessWorkerActivationStartupRequest Request) CreateRequest(
            StatelessWorkerGrainContext context,
            ActivationStartupScenario scenario,
            string payload,
            string requestContextValue)
        {
            var request = new StatelessWorkerActivationStartupRequest(scenario, payload);
            var message = Services.GetRequiredService<MessageFactory>()
                .CreateMessage(request, InvokeMethodOptions.OneWay);
            message.SetInfiniteTimeToLive();
            message.RequestContextData = new()
            {
                [ActivationStartupTestHooks.RequestContextKey] = requestContextValue,
            };
            message.SendingGrain = GrainId.Create(
                "stateless-worker-activation-startup-sender",
                Guid.NewGuid().ToString("N"));
            message.SendingSilo = ((InProcessSiloHandle)HostedCluster.Primary!).SiloAddress;
            message.TargetGrain = context.GrainId;
            message.TargetSilo = ((InProcessSiloHandle)HostedCluster.Primary!).SiloAddress;
            return (message, request);
        }

        internal IDisposable ObserveEvents() =>
            new CompositeDisposable(
                GrainLifecycleEvents.AllEvents.Subscribe(new LifecycleObserver(Hooks)),
                StatelessWorkerEvents.AllEvents.Subscribe(new StatelessWorkerObserver(Hooks)));

        internal void RemoveScenario(GrainId grainId)
        {
            CatalogHooks.RemoveGate(grainId);
            Hooks.RemoveScenario(grainId);
        }

        private sealed class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Services.AddSingleton<ActivationStartupTestHooks>();
                hostBuilder.Services.AddSingleton<CatalogActivationStartupTestHooks>();
                hostBuilder.Services.AddScoped<ActivationStartupScopedResource>();
                hostBuilder.Services.AddSingleton<StatelessWorkerActivationStartupTestActivator>();
                hostBuilder.Services.AddSingleton<
                    IConfigureGrainTypeComponents,
                    StatelessWorkerActivationStartupConfigurator>();
            }
        }

        private sealed class StatelessWorkerActivationStartupConfigurator(
            GrainClassMap grainClassMap,
            StatelessWorkerActivationStartupTestActivator activator)
            : IConfigureGrainTypeComponents
        {
            public void Configure(
                GrainType grainType,
                GrainProperties properties,
                GrainTypeSharedContext shared)
            {
                if (grainClassMap.TryGetGrainClass(grainType, out var grainClass)
                    && grainClass == typeof(StatelessWorkerActivationStartupTestGrain))
                {
                    shared.SetComponent<IGrainActivator>(activator);
                }
            }
        }

        private sealed class StatelessWorkerActivationStartupTestActivator(
            ActivationStartupTestHooks hooks,
            CatalogActivationStartupTestHooks catalogHooks) : IGrainActivator
        {
            public object CreateInstance(IGrainContext context)
            {
                var scenario = hooks.GetRequiredScenario(context.GrainId);
                scenario.ObserveCreate(context);
                context.ActivationServices
                    .GetRequiredService<ActivationStartupScopedResource>()
                    .Attach(context);
                var instance = new StatelessWorkerActivationStartupTestGrain(context, hooks);

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

        private sealed class LifecycleObserver(ActivationStartupTestHooks hooks)
            : IObserver<GrainLifecycleEvents.LifecycleEvent>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(GrainLifecycleEvents.LifecycleEvent value)
            {
                if (!hooks.TryGetScenario(value.GrainContext.GrainId, out var scenario))
                {
                    return;
                }

                var name = value switch
                {
                    GrainLifecycleEvents.Created => "Created",
                    GrainLifecycleEvents.Activated => "Activated",
                    GrainLifecycleEvents.Deactivating => "Deactivating",
                    GrainLifecycleEvents.Deactivated => "Deactivated",
                    _ => null,
                };
                if (name is not null)
                {
                    scenario!.Record(name, value.GrainContext);
                }
            }
        }

        private sealed class StatelessWorkerObserver(ActivationStartupTestHooks hooks)
            : IObserver<StatelessWorkerEvents.StatelessWorkerEvent>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(StatelessWorkerEvents.StatelessWorkerEvent value)
            {
                if (value is StatelessWorkerEvents.WorkerCreated created
                    && hooks.TryGetScenario(created.GrainId, out var scenario))
                {
                    scenario!.Record("WorkerCreated", created.WorkerContext);
                }
            }
        }

        private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
        {
            public void Dispose()
            {
                foreach (var disposable in disposables)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    internal sealed class StatelessWorkerActivationStartupRequest(
        ActivationStartupScenario scenario,
        string payload) : IInvokable
    {
        private static readonly MethodInfo Method =
            typeof(IStatelessWorkerActivationStartupTestGrain)
                .GetMethod(nameof(IStatelessWorkerActivationStartupTestGrain.Invoke))!;

        private IStatelessWorkerActivationStartupTestGrain? _target;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _result;

        public Task Completion => _completion.Task;

        public string? Result => Volatile.Read(ref _result);

        public object? GetTarget() => _target;

        public void SetTarget(ITargetHolder holder)
        {
            _target = (IStatelessWorkerActivationStartupTestGrain)holder.GetTarget()!;
        }

        public async ValueTask<Response> Invoke()
        {
            var result = await _target!.Invoke(payload);
            Volatile.Write(ref _result, result);
            scenario.Record("Response", ((IGrainBase)_target).GrainContext);
            _completion.TrySetResult();
            return Response.FromResult(result);
        }

        public int GetArgumentCount() => 1;

        public object? GetArgument(int index) =>
            index == 0 ? payload : throw new ArgumentOutOfRangeException(nameof(index));

        public void SetArgument(int index, object value) =>
            throw new NotSupportedException("The stateless-worker startup request is immutable.");

        public string GetMethodName() => nameof(IStatelessWorkerActivationStartupTestGrain.Invoke);

        public string GetInterfaceName() =>
            typeof(IStatelessWorkerActivationStartupTestGrain).FullName!;

        public string GetActivityName() => $"{GetInterfaceName()}/{GetMethodName()}";

        public MethodInfo GetMethod() => Method;

        public Type GetInterfaceType() => typeof(IStatelessWorkerActivationStartupTestGrain);

        public void Dispose()
        {
        }
    }
}
