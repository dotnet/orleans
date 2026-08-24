#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("Migration")]
public class ActivationDataMigrationTests(ActivationDataMigrationTests.Fixture fixture) : IClassFixture<ActivationDataMigrationTests.Fixture>
{
    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary!;

    [Fact]
    public async Task TryStartMigration_ReturnsTrue_WhenActivationCanStartMigration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);

        Assert.True(activation.TryStartMigration(requestContext: null, cancellationToken));

        Assert.Equal(ActivationState.Deactivating, activation.State);

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    [Fact]
    public async Task TryStartMigration_ReturnsFalse_WhenActivationIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        var originalDeactivated = activation.Deactivated;
        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), cancellationToken);
        await originalDeactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.False(activation.TryStartMigration(requestContext: null, cancellationToken));
    }

    [Fact]
    public async Task TryDeactivateForCollection_AtomicallyStartsDeactivation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        var deactivated = activation.Deactivated;
        var reason = new DeactivationReason(DeactivationReasonCode.ActivationIdle, "test");

        var result = ((ICollectibleGrainContext)activation).TryDeactivateForCollection(
            reason,
            DateTime.UtcNow,
            TimeSpan.Zero,
            respectKeepAlive: true,
            cancellationToken);

        Assert.Equal(ActivationCollectionAction.StartedDeactivation, result.Action);
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(ActivationState.Invalid, activation.State);
    }

    [Fact]
    public async Task TryStartMigration_DoesNotAcquireActivationInstanceMonitor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);

        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLock = new ManualResetEventSlim();
        var lockHolder = Task.Factory.StartNew(
            () =>
            {
                lock (activation)
                {
                    lockAcquired.SetResult();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                    {
                        throw new TimeoutException("Timed out waiting to release the activation instance monitor.");
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        try
        {
            Assert.True(
                await Task.Run(
                    () => activation.TryStartMigration(requestContext: null, cancellationToken),
                    cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }
        finally
        {
            releaseLock.Set();
            await lockHolder.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private async Task<ActivationData> GetActivation(CancellationToken cancellationToken)
    {
        var grain = _fixture.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
        await grain.Nop().WaitAsync(cancellationToken);

        var grainId = ((GrainReference)grain).GrainId;
        var directory = PrimarySilo.SiloHost.Services.GetRequiredService<ActivationDirectory>();
        return Assert.IsType<ActivationData>(directory.FindTarget(grainId));
    }

    [Fact]
    public async Task TryStartMigrationReturnsFalseDuringSynchronousActivationStartup()
    {
        var startupFixture = new SynchronousMigrationFixture();
        await startupFixture.InitializeAsync();
        var (grainId, scenario) = startupFixture.CreateScenario();
        using var lifecycleSubscription = startupFixture.ObserveLifecycle();
        Task<ActivationData>? startTask = null;
        ActivationData? context = null;

        try
        {
            startTask = Task.Run(() => startupFixture.StartActivation(grainId));
            var observation = await startupFixture.Probe.Observation.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            context = observation.Context;

            Assert.False(observation.Result);
            Assert.Equal(ActivationState.Creating, context.State);
            Assert.Same(context, startupFixture.ActivationDirectory.FindTarget(grainId));
            Assert.Equal(1, scenario.CreateCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(0, scenario.OnActivateCount);
            Assert.Equal(0, scenario.GetEventCount("Deactivating"));
            Assert.Equal(0, scenario.GetEventCount("Deactivated"));
            Assert.Equal(0, scenario.DisposeStartedCount);

            startupFixture.Probe.Release();
            Assert.Same(
                context,
                await startTask.WaitAsync(
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken));
            await scenario.WaitForEvent("Activated").WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var (message, request) = startupFixture.CreateRequest(
                context,
                scenario,
                payload: "synchronous-migration",
                requestContextValue: "synchronous-migration-context");
            context.ReceiveMessage(message);
            await scenario.WaitForEvent("Response").WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                "synchronous-migration:synchronous-migration-context",
                request.Result);
            Assert.Equal(ActivationState.Valid, context.State);
            Assert.Equal(1, scenario.GetEventCount("Created"));
            Assert.Equal(1, scenario.GetEventCount("Activated"));
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(1, scenario.RequestInvocationCount);
            Assert.Same(context, startupFixture.ActivationDirectory.FindTarget(grainId));
            Assert.Single(scenario.Events.Select(entry => entry.ActivationId).Distinct());
            Assert.All(scenario.Events, entry => Assert.Equal(grainId, entry.GrainId));
            AssertEventOrder(
                scenario,
                "SynchronousMigrationAttempted",
                "Created",
                "LifecycleStartLow",
                "LifecycleStartHigh",
                "OnActivateEntered",
                "OnActivateCompleted",
                "Activated",
                "RequestInvoked",
                "Response");
        }
        finally
        {
            startupFixture.Probe.Release();
            scenario.ReleaseActivation();
            scenario.ReleaseDisposal();
            await AwaitIgnoringFailure(startTask);
            await CleanupAsync(startupFixture.ActivationDirectory, context, scenario);
            startupFixture.Hooks.RemoveScenario(grainId);
            await startupFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryStartMigrationReturnsFalseDuringAsyncActivationStartupAndSucceedsAfterActivation()
    {
        var startupFixture = new ActivationStartupTestFixture();
        await startupFixture.InitializeAsync();
        var (grainId, scenario) = startupFixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Synchronous);
        using var lifecycleProbe = new MigrationLifecycleProbe(scenario);
        ActivationData? context = null;

        try
        {
            context = startupFixture.StartActivation(grainId);
            await scenario.WaitForEvent("OnActivateEntered").WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(ActivationState.Activating, context.State);
            Assert.False(context.TryStartMigration(requestContext: null));
            Assert.Equal(ActivationState.Activating, context.State);
            Assert.Same(context, startupFixture.ActivationDirectory.FindTarget(grainId));
            Assert.DoesNotContain(
                lifecycleProbe.Events,
                static evt => evt is GrainLifecycleEvents.Deactivating);
            Assert.DoesNotContain(
                lifecycleProbe.Events,
                static evt => evt is GrainLifecycleEvents.Deactivated);
            Assert.Equal(0, scenario.DisposeStartedCount);
            Assert.Equal(0, scenario.ScopeDisposeCount);

            scenario.ReleaseActivation();
            await scenario.WaitForEvent("Activated").WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.Equal(ActivationState.Valid, context.State);

            var deactivating = scenario.WaitForEvent("Deactivating");
            var deactivated = scenario.WaitForEvent("Deactivated");
            Assert.True(context.TryStartMigration(requestContext: null));
            await deactivating.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(ActivationState.Deactivating, context.State);
            var migration = Assert.Single(
                lifecycleProbe.Events.OfType<GrainLifecycleEvents.Deactivating>());
            Assert.Same(context, migration.GrainContext);
            Assert.Equal(DeactivationReasonCode.Migrating, migration.Reason.ReasonCode);

            await deactivated.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.Equal(ActivationState.Invalid, context.State);
            Assert.Null(startupFixture.ActivationDirectory.FindTarget(grainId));
            Assert.Equal(1, scenario.CreateCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(1, scenario.OnDeactivateCount);
            Assert.Equal(1, scenario.GetEventCount("Created"));
            Assert.Equal(1, scenario.GetEventCount("Activated"));
            Assert.Equal(1, scenario.GetEventCount("Deactivating"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Equal(1, scenario.DisposeStartedCount);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Single(scenario.Events.Select(entry => entry.ActivationId).Distinct());
            Assert.All(scenario.Events, entry => Assert.Equal(grainId, entry.GrainId));
            AssertEventOrder(
                scenario,
                "Created",
                "LifecycleStartLow",
                "LifecycleStartHigh",
                "OnActivateEntered",
                "OnActivateCompleted",
                "Activated",
                "Deactivating",
                "OnDeactivateEntered",
                "OnDeactivateCompleted",
                "LifecycleStopHigh",
                "LifecycleStopLow",
                "ActivatorDisposeStarted",
                "ActivatorDisposeCompleted",
                "ScopeDisposed",
                "Deactivated");
        }
        finally
        {
            scenario.ReleaseActivation();
            scenario.ReleaseDisposal();
            await CleanupAsync(startupFixture.ActivationDirectory, context, scenario);
            startupFixture.RemoveScenario(grainId);
            await startupFixture.DisposeAsync();
        }
    }

    private static void AssertEventOrder(
        ActivationStartupScenario scenario,
        params string[] expected)
    {
        var expectedNames = expected.ToHashSet();
        Assert.Equal(
            expected,
            scenario.Events
                .Where(entry => expectedNames.Contains(entry.Name))
                .Select(entry => entry.Name));
        Assert.All(expected, name => Assert.Equal(1, scenario.GetEventCount(name)));
    }

    private static async Task CleanupAsync(
        ActivationDirectory directory,
        ActivationData? context,
        ActivationStartupScenario scenario)
    {
        if (context is null || context.State is ActivationState.Invalid)
        {
            return;
        }

        var deactivated = scenario.WaitForEvent("Deactivated");
        context.Deactivate(new(
            DeactivationReasonCode.ApplicationRequested,
            "Migration startup test cleanup."));
        await deactivated.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Null(directory.FindTarget(context.GrainId));
    }

    private static async Task AwaitIgnoringFailure(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
        }
        catch
        {
        }
    }

    private sealed class MigrationLifecycleProbe :
        IObserver<GrainLifecycleEvents.LifecycleEvent>,
        IDisposable
    {
        private readonly ActivationStartupScenario _scenario;
        private readonly IDisposable _subscription;
        private readonly List<GrainLifecycleEvents.LifecycleEvent> _events = [];

        public MigrationLifecycleProbe(ActivationStartupScenario scenario)
        {
            _scenario = scenario;
            _subscription = GrainLifecycleEvents.AllEvents.Subscribe(this);
        }

        public IReadOnlyList<GrainLifecycleEvents.LifecycleEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(GrainLifecycleEvents.LifecycleEvent value)
        {
            if (!value.GrainContext.GrainId.Equals(_scenario.GrainId))
            {
                return;
            }

            lock (_events)
            {
                _events.Add(value);
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
                _scenario.Record(name, value.GrainContext);
            }
        }

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class SynchronousMigrationFixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)HostedCluster.Primary!;

        private IServiceProvider Services => PrimarySilo.SiloHost.Services;

        public ActivationStartupTestHooks Hooks =>
            Services.GetRequiredService<ActivationStartupTestHooks>();

        public SynchronousMigrationProbe Probe =>
            Services.GetRequiredService<SynchronousMigrationProbe>();

        public ActivationDirectory ActivationDirectory =>
            Services.GetRequiredService<ActivationDirectory>();

        public (GrainId GrainId, ActivationStartupScenario Scenario) CreateScenario()
        {
            var grainType = Services.GetRequiredService<GrainTypeResolver>()
                .GetGrainType(typeof(ActivationStartupTestGrain));
            var grainId = GrainId.Create(grainType, Guid.NewGuid().ToString("N"));
            return (
                grainId,
                Hooks.CreateScenario(
                    grainId,
                    ActivationStartupCompletion.ImmediateSuccess,
                    ActivationStartupDisposal.Synchronous));
        }

        public ActivationData StartActivation(GrainId grainId) =>
            Assert.IsType<ActivationData>(
                Services.GetRequiredService<Catalog>()
                    .GetOrCreateActivation(
                        grainId,
                        requestContextData: null,
                        rehydrationContext: null));

        public (Message Message, ActivationStartupRequest Request) CreateRequest(
            ActivationData context,
            ActivationStartupScenario scenario,
            string payload,
            string requestContextValue)
        {
            var request = new ActivationStartupRequest(scenario, payload, recordResponse: true);
            var message = Services.GetRequiredService<MessageFactory>()
                .CreateMessage(request, InvokeMethodOptions.OneWay);
            message.SetInfiniteTimeToLive();
            message.RequestContextData = new()
            {
                [ActivationStartupTestHooks.RequestContextKey] = requestContextValue,
            };
            message.SendingGrain = GrainId.Create(
                "migration-startup-sender",
                Guid.NewGuid().ToString("N"));
            message.SendingSilo = PrimarySilo.SiloAddress;
            message.TargetGrain = context.GrainId;
            message.TargetSilo = PrimarySilo.SiloAddress;
            return (message, request);
        }

        public IDisposable ObserveLifecycle() =>
            GrainLifecycleEvents.AllEvents.Subscribe(new LifecycleObserver(Hooks));

        private sealed class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Services.AddSingleton<ActivationStartupTestHooks>();
                hostBuilder.Services.AddSingleton<SynchronousMigrationProbe>();
                hostBuilder.Services.AddScoped<ActivationStartupScopedResource>();
                hostBuilder.Services.AddSingleton<SynchronousMigrationActivator>();
                hostBuilder.Services.AddSingleton<
                    IConfigureGrainTypeComponents,
                    ActivationStartupConfigurator>();
            }
        }

        private sealed class ActivationStartupConfigurator(
            GrainClassMap grainClassMap,
            SynchronousMigrationActivator activator)
            : IConfigureGrainTypeComponents
        {
            public void Configure(
                GrainType grainType,
                GrainProperties properties,
                GrainTypeSharedContext shared)
            {
                if (grainClassMap.TryGetGrainClass(grainType, out var grainClass)
                    && grainClass == typeof(ActivationStartupTestGrain))
                {
                    shared.SetComponent<IGrainActivator>(activator);
                }
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
    }

    private sealed class SynchronousMigrationProbe
    {
        private readonly TaskCompletionSource<(ActivationData Context, bool Result)> _observation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(ActivationData Context, bool Result)> Observation => _observation.Task;

        public void ObserveAndWait(ActivationData context, bool result)
        {
            _observation.TrySetResult((context, result));
            try
            {
                _release.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out releasing synchronous migration probe for grain '{context.GrainId}', activation '{context.ActivationId}', result '{result}'.",
                    exception);
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class SynchronousMigrationActivator(
        ActivationStartupTestHooks hooks,
        SynchronousMigrationProbe probe) : IGrainActivator
    {
        public object CreateInstance(IGrainContext context)
        {
            var scenario = hooks.GetRequiredScenario(context.GrainId);
            scenario.ObserveCreate(context);
            context.ActivationServices.GetRequiredService<ActivationStartupScopedResource>()
                .Attach(context);
            var instance = new ActivationStartupTestGrain(context, hooks);
            var activation = Assert.IsType<ActivationData>(context);
            var result = activation.TryStartMigration(requestContext: null);
            scenario.Record("SynchronousMigrationAttempted", context);
            probe.ObserveAndWait(activation, result);
            return instance;
        }

        public ValueTask DisposeInstance(IGrainContext context, object instance)
        {
            var scenario = hooks.GetRequiredScenario(context.GrainId);
            scenario.ObserveDisposeStarted(context);
            scenario.ObserveDisposeCompleted(context);
            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
        }
    }
}
