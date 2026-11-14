using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public class ActivationDataLifecycleTests(ActivationDataLifecycleTests.Fixture fixture) : IClassFixture<ActivationDataLifecycleTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(CompositionGrain))]
    [InlineData(typeof(UnrelatedBaseGrain))]
    [InlineData(typeof(CustomActivatedGrain))]
    [InlineData(typeof(ParticipatingGrain))]
    public async Task Setup_ObservesCompletedConstructionAndPrecedesLifecycle(Type grainClass)
    {
        var grain = fixture.GetGrain(grainClass);
        var call = grain.Ping();
        var state = await fixture.ReadState(grain);
        try
        {
            await state.SetupEntered.Task.WaitAsync(Timeout, Cancellation);
            Assert.True(state.ConstructorCompleted);
            Assert.Same(state.Context.GrainInstance, state.Feature!.ConstructorInstance);
            Assert.Equal(
                grainClass == typeof(ParticipatingGrain)
                    ? ["grain-constructed", "setup-first", "feature-constructed", "feature-participate", "setup-second", "grain-participate"]
                    : new[] { "grain-constructed", "setup-first", "feature-constructed", "feature-participate", "setup-second" },
                state.Events);
            await state.PeerSetupEntered.Task.WaitAsync(Timeout, Cancellation);
            Assert.False(state.LaterStageStarted);
            Assert.False(state.Activated);
            Assert.False(call.IsCompleted);
            Assert.Equal(grainClass == typeof(CustomActivatedGrain), state.CustomActivatorUsed);
        }
        finally
        {
            state.SetupRelease.TrySetResult();
        }

        await call.WaitAsync(Timeout, Cancellation);
        Assert.True(state.LaterStageStarted);
        Assert.True(state.Activated);
        Assert.Equal(1, state.Feature!.ParticipationCount);
        Assert.Throws<InvalidOperationException>(() => state.Context.Shared.AddActivationSetup(_ => Assert.Fail("Late setup")));
        await Deactivate(state);
        Assert.Equal(["later-stop", "setup-stop"], state.StopEvents);
        Assert.Equal(1, state.GrainDisposals);
        Assert.Equal(1, state.Feature.Disposals);
        Assert.Equal(1, state.ScopeDisposals);
    }

    [Fact]
    public async Task CachedSetup_ReusesInjectedScopedServiceAndIsolatesFreshActivations()
    {
        var grain = fixture.GetGrain(typeof(InjectedGrain));
        var first = await Activate(grain);
        Assert.Same(first.Injected, first.Feature);
        Assert.Null(first.Feature!.ConstructorInstance);
        Assert.Equal(
            ["feature-constructed", "grain-constructed", "setup-first", "feature-participate", "setup-second"],
            first.Events);
        await grain.Ping().WaitAsync(Timeout, Cancellation);
        Assert.Equal(1, first.Feature.ParticipationCount);

        var other = await Activate(fixture.GetGrain(typeof(InjectedGrain)));
        Assert.NotSame(first.Feature, other.Feature);
        Assert.NotSame(first.Context.ActivationServices, other.Context.ActivationServices);
        await Deactivate(first);
        var replacement = await Activate(grain);
        Assert.NotSame(first.Context, replacement.Context);
        Assert.NotSame(first.Feature, replacement.Feature);
        Assert.Same(first.Context.Shared, replacement.Context.Shared);
        Assert.Equal(1, replacement.Feature!.ParticipationCount);
        Assert.Equal(1, fixture.Services.GetRequiredService<ProbeRegistry>().Configurations[first.Context.GrainId.Type]);
        await Deactivate(other);
        await Deactivate(replacement);
    }

    [Fact]
    public async Task UnselectedGrain_LeavesOrdinaryAndFeatureServicesUnresolved()
    {
        var state = await Activate(fixture.GetGrain(typeof(UnselectedGrain)));
        Assert.Null(state.Feature);
        Assert.Equal(["grain-constructed", "grain-participate"], state.Events);
        Assert.Equal(1, Assert.IsType<UnselectedGrain>(state.Context.GrainInstance).ParticipationCount);
        Assert.Equal(0, fixture.Services.GetRequiredService<UnexpectedResolutions>().Count);
        await Deactivate(state);
    }

    [Theory]
    [InlineData(typeof(ConstructorFailureGrain), false)]
    [InlineData(typeof(SetupFailureGrain), true)]
    public async Task Failure_CleansUpConstructedGrainAndScope(Type grainClass, bool grainConstructed)
    {
        var grain = fixture.GetGrain(grainClass);
        var call = grain.Ping();
        var state = await fixture.ReadState(grain);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(Timeout, Cancellation));
        Assert.Contains("Injected failure", error.ToString());
        await state.Context.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Invalid, state.Context.State);
        Assert.Equal(grainConstructed ? 1 : 0, state.GrainDisposals);
        Assert.Equal(1, state.ScopeDisposals);
        if (grainConstructed)
        {
            Assert.NotNull(state.Context.GrainInstance);
            Assert.Equal(1, state.Feature!.Disposals);
            Assert.Equal(1, state.Feature.ParticipationCount);
            Assert.Equal(["grain-constructed", "setup-first", "feature-constructed", "feature-participate"], state.Events);
        }
        else
        {
            Assert.Null(state.Context.GrainInstance);
            Assert.Null(state.Feature);
            Assert.Empty(state.Events);
        }

        Assert.False(state.Activated);
        Assert.False(state.SetupEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task StatelessWorkers_RunSharedSetupForEachActualActivation()
    {
        var grain = fixture.GetGrain(typeof(CompositionWorkerGrain));
        var firstCall = grain.Ping();
        var first = await fixture.ReadState(grain);
        ProbeState? second = null;
        try
        {
            await first.SetupEntered.Task.WaitAsync(Timeout, Cancellation);
            var secondCall = grain.Ping();
            second = await fixture.ReadState(grain);
            await second.SetupEntered.Task.WaitAsync(Timeout, Cancellation);
            Assert.NotSame(first.Context, second.Context);
            Assert.Same(first.Context.Shared, second.Context.Shared);
            Assert.NotSame(first.Feature, second.Feature);
            Assert.Equal(1, first.Feature!.ParticipationCount);
            Assert.Equal(1, second.Feature!.ParticipationCount);
            Assert.Same(first.Context.GrainInstance, first.Feature.ConstructorInstance);
            Assert.Same(second.Context.GrainInstance, second.Feature.ConstructorInstance);
            Assert.Equal(1, fixture.Services.GetRequiredService<ProbeRegistry>().Configurations[first.Context.GrainId.Type]);
            first.SetupRelease.TrySetResult();
            second.SetupRelease.TrySetResult();
            await Task.WhenAll(firstCall, secondCall).WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            first.SetupRelease.TrySetResult();
            second?.SetupRelease.TrySetResult();
        }

        await Deactivate(first);
        await Deactivate(second!);
    }

    [Fact]
    public async Task NoSetup_PreservesGrainObjectEnrollmentAndOrdinaryRegistrations()
    {
        await using var isolated = new Fixture { RegisterSetup = false };
        await isolated.InitializeAsync();
        var grain = isolated.GetGrain(typeof(ParticipatingGrain));
        var call = grain.Ping();
        var state = await isolated.ReadState(grain);
        await call.WaitAsync(Timeout, Cancellation);
        Assert.Null(state.Feature);
        Assert.Equal(["grain-constructed", "grain-participate"], state.Events);
        Assert.Equal(1, Assert.IsType<ParticipatingGrain>(state.Context.GrainInstance).ParticipationCount);
        Assert.Equal(0, isolated.Services.GetRequiredService<UnexpectedResolutions>().Count);
        await Deactivate(state);
    }

    private async Task<ProbeState> Activate(ILifecycleCompositionGrain grain)
    {
        var call = grain.Ping();
        var state = await fixture.ReadState(grain);
        state.SetupRelease.TrySetResult();
        await call.WaitAsync(Timeout, Cancellation);
        return state;
    }

    private static async Task Deactivate(ProbeState state)
    {
        state.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Lifecycle composition test"));
        await state.Context.Deactivated.WaitAsync(Timeout, Cancellation);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public bool RegisterSetup { get; init; } = true;
        public IServiceProvider Services => HostedCluster.Silos[0].ServiceProvider;

        public ILifecycleCompositionGrain GetGrain(Type grainClass) =>
            GrainFactory.GetGrain<ILifecycleCompositionGrain>(Guid.NewGuid(), grainClass.FullName);

        public async Task<ProbeState> ReadState(ILifecycleCompositionGrain grain) =>
            await Services.GetRequiredService<ProbeRegistry>().Get(grain.GetGrainId()).Reader.ReadAsync(Cancellation).AsTask().WaitAsync(Timeout, Cancellation);

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureSilo((_, silo) =>
            {
                var services = silo.Services;
                services.AddSingleton<ProbeRegistry>();
                services.AddScoped<ProbeState>();
                services.AddScoped<ScopedFeature>();
                services.AddSingleton<UnexpectedResolutions>();
                services.AddScoped<UnusedParticipant>(provider => Unexpected(provider));
                services.AddScoped<ILifecycleParticipant<IGrainLifecycle>>(provider => Unexpected(provider));
                services.AddKeyedScoped<ILifecycleParticipant<IGrainLifecycle>>("unused", (provider, _) => Unexpected(provider));
                if (RegisterSetup)
                {
                    services.AddSingleton<IConfigureGrainTypeComponents, SetupConfigurator>();
                    services.AddSingleton<IConfigureGrainTypeComponents, SecondSetupConfigurator>();
                }
            });
        }

        private static UnusedParticipant Unexpected(IServiceProvider provider)
        {
            Interlocked.Increment(ref provider.GetRequiredService<UnexpectedResolutions>().Count);
            throw new InvalidOperationException("Unexpected resolution of an ordinary lifecycle participant registration.");
        }

        public override async ValueTask DisposeAsync()
        {
            foreach (var state in Services.GetRequiredService<ProbeRegistry>().States)
            {
                state.SetupRelease.TrySetResult();
            }

            await base.DisposeAsync();
        }
    }

    public sealed class ProbeRegistry
    {
        private readonly ConcurrentDictionary<GrainId, Channel<ProbeState>> _created = new();
        public ConcurrentBag<ProbeState> States { get; } = [];
        public ConcurrentDictionary<GrainType, int> Configurations { get; } = new();
        public Channel<ProbeState> Get(GrainId id) => _created.GetOrAdd(id, _ => Channel.CreateUnbounded<ProbeState>());
        public void Add(ProbeState state)
        {
            States.Add(state);
            Assert.True(Get(state.Context.GrainId).Writer.TryWrite(state));
        }
    }

    public sealed class ProbeState : IDisposable
    {
        public ProbeState(IGrainContext context, ProbeRegistry registry)
        {
            Context = Assert.IsType<ActivationData>(context);
            registry.Add(this);
        }

        internal ActivationData Context { get; }
        public List<string> Events { get; } = [];
        public List<string> StopEvents { get; } = [];
        public TaskCompletionSource SetupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PeerSetupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SetupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ScopedFeature? Feature { get; set; }
        public ScopedFeature? Injected { get; set; }
        public bool ConstructorCompleted { get; set; }
        public bool CustomActivatorUsed { get; set; }
        public bool LaterStageStarted { get; set; }
        public bool Activated { get; set; }
        public int GrainDisposals { get; set; }
        public int ScopeDisposals { get; private set; }
        public void Dispose() => ScopeDisposals++;
        public void Constructed()
        {
            ConstructorCompleted = true;
            Events.Add("grain-constructed");
        }
    }

    public sealed class ScopedFeature : ILifecycleParticipant<IGrainLifecycle>, IDisposable
    {
        private readonly ProbeState _state;
        public ScopedFeature(ProbeState state)
        {
            _state = state;
            ConstructorInstance = state.Context.GrainInstance;
            state.Feature = this;
            state.Events.Add("feature-constructed");
        }

        public object? ConstructorInstance { get; }
        public int ParticipationCount { get; private set; }
        public int Disposals { get; private set; }

        public void Participate(IGrainLifecycle lifecycle)
        {
            ParticipationCount++;
            Assert.True(_state.ConstructorCompleted);
            Assert.NotNull(_state.Context.GrainInstance);
            _state.Events.Add("feature-participate");
            lifecycle.Subscribe<ScopedFeature>(GrainLifecycleStage.SetupState, async cancellation =>
            {
                _state.SetupEntered.TrySetResult();
                await _state.SetupRelease.Task.WaitAsync(cancellation);
            }, _ =>
            {
                _state.StopEvents.Add("setup-stop");
                return Task.CompletedTask;
            });
        }

        public void Dispose() => Disposals++;
    }

    public sealed class UnexpectedResolutions
    {
        public int Count;
    }

    public sealed class UnusedParticipant : ILifecycleParticipant<IGrainLifecycle>
    {
        public void Participate(IGrainLifecycle lifecycle) => Assert.Fail("Ordinary DI registrations remain explicitly enrolled.");
    }

    private static bool Selected(GrainClassMap map, GrainType grainType) =>
        map.TryGetGrainClass(grainType, out var type)
        && typeof(ILifecycleCompositionGrain).IsAssignableFrom(type)
        && type != typeof(UnselectedGrain);

    private sealed class SetupConfigurator(GrainClassMap map, ProbeRegistry registry) : IConfigureGrainTypeComponents
    {
        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (!Selected(map, grainType)) return;
            registry.Configurations.AddOrUpdate(grainType, 1, (_, count) => count + 1);
            Assert.Throws<ArgumentNullException>(() => shared.AddActivationSetup(null!));
            shared.AddActivationSetup(static context =>
            {
                var state = context.ActivationServices.GetRequiredService<ProbeState>();
                Assert.True(state.ConstructorCompleted);
                Assert.NotNull(context.GrainInstance);
                state.Events.Add("setup-first");
                context.ActivationServices.GetRequiredService<ScopedFeature>().Participate(context.ObservableLifecycle);
                if (context.GrainInstance is SetupFailureGrain)
                {
                    throw new InvalidOperationException("Injected failure: setup.");
                }
            });
            if (map.TryGetGrainClass(grainType, out var grainClass) && grainClass == typeof(CustomActivatedGrain))
            {
                shared.SetComponent<IGrainActivator>(new CustomActivator());
            }
        }
    }

    private sealed class SecondSetupConfigurator(GrainClassMap map) : IConfigureGrainTypeComponents
    {
        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (!Selected(map, grainType)) return;
            shared.AddActivationSetup(static context =>
            {
                var state = context.ActivationServices.GetRequiredService<ProbeState>();
                state.Events.Add("setup-second");
                context.ObservableLifecycle.Subscribe<SecondSetupConfigurator>(GrainLifecycleStage.SetupState, _ =>
                {
                    state.PeerSetupEntered.TrySetResult();
                    return Task.CompletedTask;
                });
                context.ObservableLifecycle.Subscribe<SecondSetupConfigurator>(GrainLifecycleStage.SetupState + 1, _ =>
                {
                    Assert.True(state.SetupRelease.Task.IsCompletedSuccessfully);
                    state.LaterStageStarted = true;
                    Assert.Throws<InvalidOperationException>(() => context.ObservableLifecycle.Subscribe<UnusedParticipant>(GrainLifecycleStage.Last, _ => Task.CompletedTask));
                    return Task.CompletedTask;
                }, _ =>
                {
                    state.StopEvents.Add("later-stop");
                    return Task.CompletedTask;
                });
            });
        }
    }

    private sealed class CustomActivator : IGrainActivator
    {
        public object CreateInstance(IGrainContext context)
        {
            var state = context.ActivationServices.GetRequiredService<ProbeState>();
            state.CustomActivatorUsed = true;
            return new CustomActivatedGrain(state);
        }

        public ValueTask DisposeInstance(IGrainContext context, object instance)
        {
            ((CustomActivatedGrain)instance).Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public interface ILifecycleCompositionGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class CompositionGrain : Grain, ILifecycleCompositionGrain, IDisposable
{
    protected ActivationDataLifecycleTests.ProbeState State { get; }
    public CompositionGrain(ActivationDataLifecycleTests.ProbeState state)
    {
        State = state;
        state.Constructed();
    }

    public Task Ping() => Task.CompletedTask;
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        State.Activated = true;
        return Task.CompletedTask;
    }

    public void Dispose() => State.GrainDisposals++;
}

public abstract class UnrelatedApplicationBase;

public sealed class UnrelatedBaseGrain : UnrelatedApplicationBase, ILifecycleCompositionGrain, IGrainBase, IDisposable
{
    private readonly ActivationDataLifecycleTests.ProbeState _state;
    public UnrelatedBaseGrain(ActivationDataLifecycleTests.ProbeState state)
    {
        _state = state;
        state.Constructed();
    }

    public IGrainContext GrainContext => _state.Context;
    public Task Ping() => Task.CompletedTask;
    public Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _state.Activated = true;
        return Task.CompletedTask;
    }

    public void Dispose() => _state.GrainDisposals++;
}

public sealed class CustomActivatedGrain(ActivationDataLifecycleTests.ProbeState state) : CompositionGrain(state);
public sealed class SetupFailureGrain(ActivationDataLifecycleTests.ProbeState state) : CompositionGrain(state);

public sealed class ConstructorFailureGrain : CompositionGrain
{
    public ConstructorFailureGrain(ActivationDataLifecycleTests.ProbeState state) : base(Fail(state)) { }
    private static ActivationDataLifecycleTests.ProbeState Fail(ActivationDataLifecycleTests.ProbeState state) =>
        throw new InvalidOperationException("Injected failure: grain constructor.");
}

[StatelessWorker(2)]
public sealed class CompositionWorkerGrain(ActivationDataLifecycleTests.ProbeState state) : CompositionGrain(state);

public sealed class InjectedGrain : CompositionGrain
{
    public InjectedGrain(ActivationDataLifecycleTests.ProbeState state, ActivationDataLifecycleTests.ScopedFeature feature) : base(state)
    {
        state.Injected = feature;
    }
}

public class ParticipatingGrain(ActivationDataLifecycleTests.ProbeState state) : CompositionGrain(state), ILifecycleParticipant<IGrainLifecycle>
{
    public int ParticipationCount { get; private set; }
    public void Participate(IGrainLifecycle lifecycle)
    {
        ParticipationCount++;
        State.Events.Add("grain-participate");
    }
}

public sealed class UnselectedGrain(ActivationDataLifecycleTests.ProbeState state) : ParticipatingGrain(state);
