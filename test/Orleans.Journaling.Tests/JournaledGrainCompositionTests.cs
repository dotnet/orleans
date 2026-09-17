using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Journaling.Json;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestSuite("BVT"), TestProvider("None"), TestArea("Journaling")]
public sealed class JournaledGrainCompositionTests(JournalCompositionFixture fixture)
    : IClassFixture<JournalCompositionFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(PlainJournalGrain))]
    [InlineData(typeof(ApplicationJournalGrain))]
    [InlineData(typeof(InjectedJournalGrain))]
    [InlineData(typeof(ConvenienceJournalGrain))]
    public async Task Composition_RecoversBeforeActivationAndIsolatesFreshScopes(Type grainClass)
    {
        var grain = fixture.GetGrain(grainClass);
        Assert.Equal(new string?[] { null, null }, await grain.GetActivationValues());
        var first = await fixture.ReadProbe(grain);
        Assert.Same(first.Context.GrainInstance, first.ConstructorInstance);
        var firstObserver = Assert.IsType<JournalCompositionObserver>(first.Observer);
        Assert.Same(first.Manager, firstObserver.Manager);
        Assert.Same(firstObserver, first.Context.ActivationServices.GetRequiredService<JournalCompositionObserver>());
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted", "Activated"], firstObserver.Calls);
        Assert.Equal(new string?[] { null, null }, firstObserver.RecoveredValues);
        Assert.Empty(firstObserver.Faults);
        Assert.Throws<NotSupportedException>(() => first.Manager!.RegisterObserver(firstObserver));
        Assert.Equal(1, fixture.Storage.Get(first.Context.GrainId).Reads);
        Assert.Equal(grainClass == typeof(InjectedJournalGrain) ? 0 : 1, first.SetupCount);
        Assert.Equal(first.SetupCount, first.Feature?.ParticipationCount ?? 0);
        Assert.True(first.Manager!.TryGetState("one", out var state));
        Assert.Same(first.First, state);

        await grain.SetValues("one", "two");
        Assert.Equal(new string?[] { "one", "two" }, await grain.GetValues());
        Assert.Equal(1, fixture.Storage.Get(first.Context.GrainId).Writes);
        Assert.Equal(1, fixture.Storage.Get(first.Context.GrainId).Managers);
        Assert.Equal(new[]
        {
            "RecoveryStarted", "RecoveryCompleted", "Activated", "WriteRequested", "WritePreparing",
            "WriteFinalizing", "WriteStarted", "WriteCompleted"
        }, firstObserver.Calls);

        var other = fixture.GetGrain(grainClass);
        Assert.Equal(new string?[] { null, null }, await other.GetActivationValues());
        var isolated = await fixture.ReadProbe(other);
        Assert.NotSame(first.Manager, isolated.Manager);
        Assert.NotSame(first.First, isolated.First);
        Assert.NotSame(first.Context.ActivationServices, isolated.Context.ActivationServices);
        var isolatedObserver = Assert.IsType<JournalCompositionObserver>(isolated.Observer);
        Assert.NotSame(firstObserver, isolatedObserver);
        Assert.Same(isolated.Manager, isolatedObserver.Manager);
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted", "Activated"], isolatedObserver.Calls);

        var firstCalls = firstObserver.Calls.ToArray();
        await Deactivate(first);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, firstObserver.Disposals);
        Assert.Empty(firstObserver.Faults);
        Assert.Equal(firstCalls, firstObserver.Calls);
        Assert.Equal(first.SetupCount, first.Feature?.Disposals ?? 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Manager.InitializeAsync(Cancellation).AsTask());

        Assert.Equal(new string?[] { "one", "two" }, await grain.GetActivationValues());
        Assert.Equal(new string?[] { "one", "two" }, await grain.GetValues());
        var replacement = await fixture.ReadProbe(grain);
        Assert.NotSame(first.Manager, replacement.Manager);
        Assert.NotSame(first.First, replacement.First);
        var replacementObserver = Assert.IsType<JournalCompositionObserver>(replacement.Observer);
        Assert.NotSame(firstObserver, replacementObserver);
        Assert.Same(replacement.Manager, replacementObserver.Manager);
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted", "Activated"], replacementObserver.Calls);
        Assert.Equal(new[] { "one", "two" }, replacementObserver.RecoveredValues);
        Assert.Empty(replacementObserver.Faults);
        Assert.Equal(firstCalls, firstObserver.Calls);
        Assert.Equal(first.SetupCount, replacement.SetupCount);
        Assert.Equal(2, fixture.Storage.Get(first.Context.GrainId).Reads);
        Assert.Equal(2, fixture.Storage.Get(first.Context.GrainId).Managers);
        if (first.SetupCount == 1)
        {
            Assert.NotSame(first.Feature, replacement.Feature);
            Assert.Equal(1, fixture.Registry.Configurations[first.Context.GrainId.Type]);
            Assert.Equal(
                new[] { "constructed", "setup", "feature constructed", "before recovery", "after recovery", "activated" },
                replacement.Events);
        }

        await Deactivate(isolated);
        await Deactivate(replacement);
        Assert.Equal(1, isolatedObserver.Disposals);
        Assert.Equal(1, replacementObserver.Disposals);
        Assert.Empty(isolatedObserver.Faults);
        Assert.Empty(replacementObserver.Faults);
    }

    [Theory]
    [InlineData(typeof(JournalConstructorFailureGrain), "constructor", 0)]
    [InlineData(typeof(JournalFeatureConstructorFailureGrain), "feature constructor", 0)]
    [InlineData(typeof(JournalSetupFailureGrain), "setup", 0)]
    [InlineData(typeof(JournalActivationFailureGrain), "activation", 1)]
    public async Task Composition_PropagatesFailuresAndDisposesScope(Type grainClass, string phase, int reads)
    {
        var grain = fixture.GetGrain(grainClass);
        var call = grain.GetValues();
        var probe = await fixture.ReadProbe(grain);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(Timeout, Cancellation));
        Assert.Contains($"Expected {phase} failure.", exception.ToString(), StringComparison.Ordinal);
        await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(1, probe.Disposals);
        Assert.Equal(reads, fixture.Storage.Get(probe.Context.GrainId).Reads);
        Assert.False(probe.Activated);
        if (probe.Feature is { } feature)
        {
            Assert.Equal(1, feature.Disposals);
        }

        if (grainClass == typeof(JournalActivationFailureGrain))
        {
            var observer = Assert.IsType<JournalCompositionObserver>(probe.Observer);
            Assert.Empty(observer.Faults);
            Assert.Equal(1, observer.Disposals);
            Assert.Equal(["RecoveryStarted", "RecoveryCompleted"], observer.Calls);
        }
        else
        {
            Assert.Null(probe.Observer);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.Manager!.InitializeAsync(Cancellation).AsTask());
    }

    [Fact]
    public async Task UninvolvedGrain_CreatesNoJournalManagerOrFeature()
    {
        var grain = fixture.GetGrain(typeof(UninvolvedJournalGrain));
        Assert.Empty(await grain.GetValues());
        var probe = await fixture.ReadProbe(grain);
        Assert.Null(probe.Manager);
        Assert.Null(probe.Feature);
        Assert.Null(probe.Observer);
        Assert.Equal(0, probe.SetupCount);
        Assert.False(fixture.Storage.Contains(JournalId.FromGrainId(grain.GetGrainId())));
        await Deactivate(probe);
        Assert.Equal(1, probe.Disposals);
    }

    [Theory]
    [InlineData(typeof(PlainJournalGrain), false)]
    [InlineData(typeof(PlainJournalGrain), true)]
    [InlineData(typeof(ApplicationJournalGrain), false)]
    [InlineData(typeof(ApplicationJournalGrain), true)]
    [InlineData(typeof(InjectedJournalGrain), false)]
    [InlineData(typeof(InjectedJournalGrain), true)]
    [InlineData(typeof(ConvenienceJournalGrain), false)]
    [InlineData(typeof(ConvenienceJournalGrain), true)]
    public async Task Composition_TerminalWriteFailureNotifiesObserverAndRecoversFreshScope(Type grainClass, bool committed)
    {
        var grain = fixture.GetGrain(grainClass);
        await grain.SetValues("old one", "old two");
        var first = await fixture.ReadProbe(grain);
        var storage = fixture.Storage.Get(first.Context.GrainId);
        var observer = Assert.IsType<JournalCompositionObserver>(first.Observer);
        Assert.Same(first.Manager, observer.Manager);
        storage.FailNextWrite = committed;
        var error = await Assert.ThrowsAsync<IOException>(() => grain.SetValues("new one", "new two"));
        Assert.Equal(JournalCompositionStorage.FailureMessage, error.Message);
        var fault = Assert.IsType<IOException>(Assert.Single(observer.Faults));
        Assert.Equal(error.Message, fault.Message);
        Assert.Equal(new[]
        {
            "RecoveryStarted", "RecoveryCompleted", "Activated", "WriteRequested", "WritePreparing",
            "WriteFinalizing", "WriteStarted", "WriteCompleted", "WriteRequested", "WritePreparing",
            "WriteFinalizing", "WriteStarted", "Faulted"
        }, observer.Calls);
        await first.Context.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, observer.Disposals);
        Assert.Single(observer.Faults);
        var expected = committed ? new[] { "new one", "new two" } : new[] { "old one", "old two" };
        Assert.Equal(expected, await grain.GetActivationValues());
        Assert.Equal(expected, await grain.GetValues());
        var replacement = await fixture.ReadProbe(grain);
        Assert.NotSame(first.Manager, replacement.Manager);
        Assert.NotSame(first.First, replacement.First);
        var replacementObserver = Assert.IsType<JournalCompositionObserver>(replacement.Observer);
        Assert.NotSame(observer, replacementObserver);
        Assert.Same(replacement.Manager, replacementObserver.Manager);
        Assert.Equal(expected, replacementObserver.RecoveredValues);
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted", "Activated"], replacementObserver.Calls);
        Assert.Empty(replacementObserver.Faults);
        Assert.Equal(2, storage.Managers);
        Assert.Equal(2, storage.Reads);
        await Deactivate(replacement);
        Assert.Equal(1, replacementObserver.Disposals);
        Assert.Empty(replacementObserver.Faults);
        Assert.Single(observer.Faults);
    }

    [Fact]
    public async Task ExplicitFactory_WithAmbientGrainContextKeepsManualIndependentLifecycle()
    {
        var grain = fixture.GetGrain(typeof(PlainJournalGrain));
        await grain.SetValues("grain one", "grain two");
        var probe = await fixture.ReadProbe(grain);
        var id = new JournalId($"standalone/{Guid.NewGuid():N}");
        var factory = fixture.Services.GetRequiredService<IJournaledStateManagerFactory>();
        var codec = fixture.Services.GetRequiredKeyedService<IDurableValueCommandCodec<string>>(JsonJournalExtensions.JournalFormatKey);
        IJournaledStateManager manager;
        RuntimeContext.SetExecutionContext(probe.Context, out var previous);
        try
        {
            manager = factory.Create(id);
        }
        finally
        {
            RuntimeContext.SetExecutionContext(previous!, out _);
        }

        await using (manager)
        {
            var value = new DurableValue<string>("value", manager, codec);
            using var observer = new JournalCompositionObserver(manager, () => [value.Value]);
            manager.RegisterObserver(observer);
            Assert.Empty(observer.Calls);
            Assert.Equal(0, fixture.Storage.Get(id).Reads);
            await manager.InitializeAsync(Cancellation);
            Assert.Equal(["RecoveryStarted", "RecoveryCompleted"], observer.Calls);
            value.Value = "standalone";
            await manager.WriteStateAsync(Cancellation);
            fixture.Storage.Get(id).FailNextWrite = false;
            value.Value = "uncommitted";
            var exception = await Assert.ThrowsAsync<IOException>(() => manager.WriteStateAsync(Cancellation).AsTask());
            Assert.Same(exception, Assert.Single(observer.Faults));
            Assert.False(probe.Context.Deactivated.IsCompleted);
            Assert.Equal(new[] { "grain one", "grain two" }, await grain.GetValues());
        }

        await using (var recovered = factory.Create(id))
        {
            var value = new DurableValue<string>("value", recovered, codec);
            using var observer = new JournalCompositionObserver(recovered, () => [value.Value]);
            recovered.RegisterObserver(observer);
            Assert.Empty(observer.Calls);
            await recovered.InitializeAsync(Cancellation);
            Assert.Equal("standalone", value.Value);
            Assert.Equal(["standalone"], observer.RecoveredValues);
            Assert.Equal(["RecoveryStarted", "RecoveryCompleted"], observer.Calls);
            Assert.Empty(observer.Faults);
        }

        Assert.Equal(2, fixture.Storage.Get(id).Managers);
        Assert.Equal(2, fixture.Storage.Get(id).Reads);
        await Deactivate(probe);
    }

    [Theory]
    [InlineData("hosting")]
    [InlineData("implementation")]
    [InlineData("explicit")]
    public async Task ManagerResolution_EnrollsExactlyOnceAndDurableGrainRetainsHelpers(string registration)
    {
        var builder = CreateBuilder();
        var lifecycle = new CompositionTestLifecycle();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("composition", "factory-enrollment"));
        context.ObservableLifecycle.Returns(lifecycle);
        builder.Services.AddScoped(_ => context);
        var journalId = registration == "explicit"
            ? new JournalId("explicit/scoped-override")
            : JournalId.FromGrainId(context.GrainId);
        if (registration == "implementation")
        {
            builder.Services.AddScoped<IJournaledStateManager, JournaledStateManager>();
        }
        else if (registration == "explicit")
        {
            builder.Services.AddScoped(services =>
            {
                var manager = services.GetRequiredService<IJournaledStateManagerFactory>().Create(journalId);
                ((ILifecycleParticipant<IGrainLifecycle>)manager).Participate(
                    services.GetRequiredService<IGrainContext>().ObservableLifecycle);
                return manager;
            });
        }

        await using var services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = services.CreateAsyncScope();
        context.ActivationServices.Returns(scope.ServiceProvider);
        var scopedManager = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        Assert.Equal(1, lifecycle.Subscriptions);
        RuntimeContext.SetExecutionContext(context, out var previous);
        HelperJournalGrain grain;
        try
        {
            grain = new HelperJournalGrain();
        }
        finally
        {
            RuntimeContext.SetExecutionContext(previous!, out _);
        }

        Assert.Equal(1, lifecycle.Subscriptions);
        var value = grain.Value;
        Assert.Same(value, grain.Value);
        Assert.Same(grain.State, grain.State);
        Assert.Same(scopedManager, grain.Manager);
        using var observer = new JournalCompositionObserver(scopedManager, () => [value.Value]);
        scopedManager.RegisterObserver(observer);
        Assert.Empty(observer.Calls);
        Assert.Equal(1, lifecycle.Subscriptions);
        await lifecycle.OnStart(Cancellation);
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted"], observer.Calls);
        value.Value = "helper";
        await grain.Commit();
        await lifecycle.OnStop(Cancellation);
        Assert.Equal(new[]
        {
            "RecoveryStarted", "RecoveryCompleted", "WriteRequested", "WritePreparing",
            "WriteFinalizing", "WriteStarted", "WriteCompleted"
        }, observer.Calls);
        Assert.Empty(observer.Faults);
        Assert.Equal(1, lifecycle.Subscriptions);

        await using var recovered = services.GetRequiredService<IJournaledStateManagerFactory>().Create(journalId);
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<string>>(JsonJournalExtensions.JournalFormatKey);
        var recoveredValue = new DurableValue<string>("helper", recovered, codec);
        await recovered.InitializeAsync(Cancellation);
        Assert.Equal("helper", recoveredValue.Value);
    }

    [Fact]
    public async Task DurableGrain_UsesFactoryEnrolledCustomManager()
    {
        var manager = Substitute.For<IJournaledStateManager, ILifecycleParticipant<IGrainLifecycle>>();
        var context = Substitute.For<IGrainContext>();
        var lifecycle = new CompositionTestLifecycle();
        await using var services = new ServiceCollection()
            .AddScoped<IJournaledStateManager>(_ =>
            {
                ((ILifecycleParticipant<IGrainLifecycle>)manager).Participate(lifecycle);
                return manager;
            })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = services.CreateAsyncScope();
        context.ActivationServices.Returns(scope.ServiceProvider);
        context.ObservableLifecycle.Returns(lifecycle);
        Assert.Same(manager, scope.ServiceProvider.GetRequiredService<IJournaledStateManager>());
        ((ILifecycleParticipant<IGrainLifecycle>)manager).Received(1).Participate(lifecycle);
        RuntimeContext.SetExecutionContext(context, out var previous);
        try
        {
            var grain = new HelperJournalGrain();
            Assert.Same(manager, grain.Manager);
        }
        finally
        {
            RuntimeContext.SetExecutionContext(previous!, out _);
        }

        ((ILifecycleParticipant<IGrainLifecycle>)manager).Received(1).Participate(lifecycle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrainBoundConstruction_EnrollmentFailureDisposesManagerAndPreservesException(bool useImplementationRegistration)
    {
        var builder = CreateBuilder();
        if (useImplementationRegistration)
        {
            builder.Services.AddScoped<IJournaledStateManager, JournaledStateManager>();
        }
        builder.Services.AddSingleton<TrackingJournalFormat>();
        builder.Services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey,
            static (services, _) => services.GetRequiredService<TrackingJournalFormat>());
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("composition", "enrollment-failure"));
        var lifecycle = Substitute.For<IGrainLifecycle>();
        var expected = new InvalidOperationException("Expected enrollment failure.");
        lifecycle.When(x => x.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>()))
            .Do(_ => throw expected);
        context.ObservableLifecycle.Returns(lifecycle);
        builder.Services.AddScoped(_ => context);
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IJournaledStateManager>());
        Assert.Same(expected, error);
        var writer = Assert.Single(services.GetRequiredService<TrackingJournalFormat>().Writers);
        Assert.Throws<ObjectDisposedException>(() => writer.GetBuffer());
    }

    internal static async Task Deactivate(JournalCompositionProbe probe)
    {
        probe.Context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Journaling composition test"));
        await probe.Context.Deactivated.WaitAsync(Timeout, Cancellation);
    }

    private static CompositionSiloBuilder CreateBuilder()
    {
        var builder = new CompositionSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (services, _) => services.GetRequiredService<TimeProvider>());
        builder.AddVolatileJournalStorage().UseJsonJournalFormat(JournalingTestsJsonContext.Default);
        return builder;
    }

    private sealed class CompositionSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TrackingJournalFormat(JsonLinesJournalFormat inner) : IJournalFormat
    {
        public List<JournalBufferWriter> Writers { get; } = [];
        public string FormatKey => inner.FormatKey;
        public string? MimeType => inner.MimeType;
        public JournalBufferWriter CreateWriter()
        {
            var writer = inner.CreateWriter();
            Writers.Add(writer);
            return writer;
        }
        public void Replay(JournalBufferReader input, JournalReplayContext context) => inner.Replay(input, context);
    }

    private sealed class HelperJournalGrain : DurableGrain
    {
        public IJournaledStateManager Manager => StateManager;
        public IDurableValue<string> Value => ServiceProvider.GetRequiredKeyedService<IDurableValue<string>>("helper");
        public IJournaledState State => GetOrCreateState("helper-state", static _ => Substitute.For<IJournaledState>(), 0);
        public ValueTask Commit() => WriteStateAsync();
    }
}

internal sealed class CompositionTestLifecycle() : LifecycleSubject(NullLogger.Instance), IGrainLifecycle
{
    public int Subscriptions { get; private set; }
    public override IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
    {
        Subscriptions++;
        return base.Subscribe(observerName, stage, observer);
    }
    public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
    public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
}

public sealed class JournalCompositionFixture : IntegrationTestFixture
{
    public IServiceProvider Services => Cluster.Silos[0].ServiceProvider;
    public JournalCompositionRegistry Registry => Services.GetRequiredService<JournalCompositionRegistry>();
    public JournalCompositionStorageProvider Storage => Services.GetRequiredService<JournalCompositionStorageProvider>();

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<JournalCompositionRegistry>();
            silo.Services.AddScoped<JournalCompositionProbe>();
            silo.Services.AddScoped<JournalCompositionFeature>();
            silo.Services.AddScoped(static services => new JournalCompositionObserver(
                services.GetRequiredService<IJournaledStateManager>(),
                services.GetRequiredService<JournalCompositionProbe>().Read));
            silo.Services.AddSingleton<JournalCompositionStorageProvider>();
            silo.Services.AddSingleton<IJournalStorageProvider>(static services => services.GetRequiredService<JournalCompositionStorageProvider>());
            silo.Services.AddSingleton<IConfigureGrainTypeComponents, JournalCompositionConfigurator>();
        });
    }

    public IJournalCompositionGrain GetGrain(Type type) => Client.GetGrain<IJournalCompositionGrain>(Guid.NewGuid(), type.FullName);
    public async Task<JournalCompositionProbe> ReadProbe(IJournalCompositionGrain grain) =>
        await Registry.Get(grain.GetGrainId()).Reader.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}

public sealed class JournalCompositionRegistry
{
    private readonly ConcurrentDictionary<GrainId, Channel<JournalCompositionProbe>> _probes = new();
    public ConcurrentDictionary<GrainType, int> Configurations { get; } = new();
    public Channel<JournalCompositionProbe> Get(GrainId id) => _probes.GetOrAdd(id, static _ => Channel.CreateUnbounded<JournalCompositionProbe>());
}

public sealed class JournalCompositionProbe : IDisposable
{
    public JournalCompositionProbe(IGrainContext context, JournalCompositionRegistry registry)
    {
        Context = context;
        Assert.True(registry.Get(context.GrainId).Writer.TryWrite(this));
    }

    public IGrainContext Context { get; }
    public IJournaledStateManager? Manager { get; set; }
    public IDurableValue<string>? First { get; set; }
    public IDurableValue<string>? Second { get; set; }
    public JournalCompositionFeature? Feature { get; set; }
    public JournalCompositionObserver? Observer { get; set; }
    public object? ConstructorInstance { get; private set; }
    public List<string> Events { get; } = [];
    public int SetupCount { get; set; }
    public int Disposals { get; private set; }
    public bool Activated { get; private set; }
    public string?[] ActivationValues { get; private set; } = [];
    public void Constructed(object instance)
    {
        ConstructorInstance = instance;
        Events.Add("constructed");
    }
    public Task Activate()
    {
        if (Context.GrainInstance is JournalActivationFailureGrain)
        {
            throw new InvalidOperationException("Expected activation failure.");
        }

        ActivationValues = Read();
        Activated = true;
        Events.Add("activated");
        Observer?.Calls.Enqueue("Activated");
        return Task.CompletedTask;
    }
    public string?[] Read() => [First!.Value, Second!.Value];
    public async Task SetValues(string first, string second)
    {
        First!.Value = first;
        Second!.Value = second;
        await Manager!.WriteStateAsync(CancellationToken.None);
    }
    public void Dispose() => Disposals++;
}

public interface IJournalFeatureGrain;

internal sealed class JournalCompositionConfigurator(GrainClassMap map, JournalCompositionRegistry registry) : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (map.TryGetGrainClass(grainType, out var type)
            && (typeof(IJournalFeatureGrain).IsAssignableFrom(type) || type == typeof(InjectedJournalGrain)))
        {
            if (typeof(IJournalFeatureGrain).IsAssignableFrom(type))
            {
                registry.Configurations.AddOrUpdate(grainType, 1, static (_, count) => count + 1);
                shared.AddActivationSetup(static context =>
                {
                    var probe = context.ActivationServices.GetRequiredService<JournalCompositionProbe>();
                    Assert.Same(probe.ConstructorInstance, context.GrainInstance);
                    Assert.NotNull(context.GrainInstance);
                    probe.SetupCount++;
                    probe.Events.Add("setup");
                    context.ActivationServices.GetRequiredService<JournalCompositionFeature>().Participate(context.ObservableLifecycle);
                    if (context.GrainInstance is JournalSetupFailureGrain)
                    {
                        throw new InvalidOperationException("Expected setup failure.");
                    }
                });
            }

            shared.AddActivationSetup(static context =>
            {
                var probe = context.ActivationServices.GetRequiredService<JournalCompositionProbe>();
                Assert.Same(probe.ConstructorInstance, context.GrainInstance);
                var observer = context.ActivationServices.GetRequiredService<JournalCompositionObserver>();
                Assert.Same(probe.Manager, observer.Manager);
                Assert.Empty(observer.Calls);
                observer.Manager.RegisterObserver(observer);
                probe.Observer = observer;
            });
        }
    }
}

public sealed class JournalCompositionObserver(IJournaledStateManager manager, Func<string?[]> readState)
    : IJournaledStateObserver, IDisposable
{
    public IJournaledStateManager Manager { get; } = manager;
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<Exception> Faults { get; } = new();
    public string?[] RecoveredValues { get; private set; } = [];
    public int Disposals { get; private set; }
    public void OnRecoveryStarted() => Calls.Enqueue("RecoveryStarted");
    public void OnRecoveryCompleted()
    {
        RecoveredValues = readState();
        Calls.Enqueue("RecoveryCompleted");
    }
    public void OnWriteRequested() => Calls.Enqueue("WriteRequested");
    public ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue("WritePreparing");
        return default;
    }
    public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue("WriteFinalizing");
        return default;
    }
    public void OnWriteStarted() => Calls.Enqueue("WriteStarted");
    public void OnWriteCompleted() => Calls.Enqueue("WriteCompleted");
    public void OnFaulted(Exception exception)
    {
        Faults.Enqueue(exception);
        Calls.Enqueue("Faulted");
    }
    public void Dispose() => Disposals++;
}

public sealed class JournalCompositionFeature : ILifecycleParticipant<IGrainLifecycle>, IDisposable
{
    private readonly JournalCompositionProbe _probe;
    public JournalCompositionFeature(JournalCompositionProbe probe, IJournaledStateManager manager,
        [FromKeyedServices("one")] IDurableValue<string> first,
        [FromKeyedServices("two")] IDurableValue<string> second)
    {
        _probe = probe;
        probe.Manager = manager;
        probe.First = first;
        probe.Second = second;
        if (probe.Context.GrainInstance is JournalFeatureConstructorFailureGrain)
        {
            throw new InvalidOperationException("Expected feature constructor failure.");
        }
        probe.Feature = this;
        probe.Events.Add("feature constructed");
    }
    public int ParticipationCount { get; private set; }
    public int Disposals { get; private set; }
    public void Participate(IGrainLifecycle lifecycle)
    {
        ParticipationCount++;
        lifecycle.Subscribe<JournalCompositionFeature>(GrainLifecycleStage.SetupState - 1, _ =>
        {
            Assert.True(_probe.Manager!.TryGetState("one", out var first));
            Assert.True(_probe.Manager.TryGetState("two", out var second));
            Assert.Same(_probe.First, first);
            Assert.Same(_probe.Second, second);
            _probe.Events.Add("before recovery");
            return Task.CompletedTask;
        });
        lifecycle.Subscribe<JournalCompositionFeature>(GrainLifecycleStage.SetupState + 1, _ =>
        {
            _probe.Events.Add("after recovery");
            return Task.CompletedTask;
        });
    }
    public void Dispose() => Disposals++;
}

public interface IJournalCompositionGrain : IGrainWithGuidKey
{
    Task SetValues(string first, string second);
    Task<string?[]> GetValues();
    Task<string?[]> GetActivationValues();
}

public class PlainJournalGrain : Grain, IJournalCompositionGrain, IJournalFeatureGrain
{
    protected JournalCompositionProbe Probe { get; }
    public PlainJournalGrain(JournalCompositionProbe probe)
    {
        Probe = probe;
        probe.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken) => Probe.Activate();
    public Task SetValues(string first, string second) => Probe.SetValues(first, second);
    public Task<string?[]> GetValues() => Task.FromResult(Probe.Read());
    public Task<string?[]> GetActivationValues() => Task.FromResult(Probe.ActivationValues);
}

public abstract class JournalingApplicationBase;

public sealed class ApplicationJournalGrain : JournalingApplicationBase, IGrainBase, IJournalCompositionGrain, IJournalFeatureGrain
{
    private readonly JournalCompositionProbe _probe;
    public ApplicationJournalGrain(JournalCompositionProbe probe)
    {
        _probe = probe;
        probe.Constructed(this);
    }
    public IGrainContext GrainContext => _probe.Context;
    public Task OnActivateAsync(CancellationToken cancellationToken) => _probe.Activate();
    public Task SetValues(string first, string second) => _probe.SetValues(first, second);
    public Task<string?[]> GetValues() => Task.FromResult(_probe.Read());
    public Task<string?[]> GetActivationValues() => Task.FromResult(_probe.ActivationValues);
}

public sealed class InjectedJournalGrain : Grain, IJournalCompositionGrain
{
    private readonly JournalCompositionProbe _probe;
    public InjectedJournalGrain(JournalCompositionProbe probe, IJournaledStateManager manager,
        [FromKeyedServices("one")] IDurableValue<string> first,
        [FromKeyedServices("two")] IDurableValue<string> second)
    {
        _probe = probe;
        probe.Manager = manager;
        probe.First = first;
        probe.Second = second;
        probe.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken) => _probe.Activate();
    public Task SetValues(string first, string second) => _probe.SetValues(first, second);
    public Task<string?[]> GetValues() => Task.FromResult(_probe.Read());
    public Task<string?[]> GetActivationValues() => Task.FromResult(_probe.ActivationValues);
}

public sealed class ConvenienceJournalGrain : DurableGrain, IJournalCompositionGrain, IJournalFeatureGrain
{
    private readonly JournalCompositionProbe _probe;
    public ConvenienceJournalGrain(JournalCompositionProbe probe)
    {
        _probe = probe;
        probe.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken) => _probe.Activate();
    public Task SetValues(string first, string second) => _probe.SetValues(first, second);
    public Task<string?[]> GetValues() => Task.FromResult(_probe.Read());
    public Task<string?[]> GetActivationValues() => Task.FromResult(_probe.ActivationValues);
}

public sealed class JournalConstructorFailureGrain : PlainJournalGrain
{
    public JournalConstructorFailureGrain(JournalCompositionProbe probe, IJournaledStateManager manager) : base(probe)
    {
        probe.Manager = manager;
        throw new InvalidOperationException("Expected constructor failure.");
    }
}
public sealed class JournalFeatureConstructorFailureGrain(JournalCompositionProbe probe) : PlainJournalGrain(probe);
public sealed class JournalSetupFailureGrain(JournalCompositionProbe probe) : PlainJournalGrain(probe);
public sealed class JournalActivationFailureGrain(JournalCompositionProbe probe) : PlainJournalGrain(probe);

public sealed class UninvolvedJournalGrain(JournalCompositionProbe probe) : Grain, IJournalCompositionGrain
{
    public Task SetValues(string first, string second) => throw new NotSupportedException();
    public Task<string?[]> GetValues()
    {
        probe.Constructed(this);
        return Task.FromResult(Array.Empty<string?>());
    }
    public Task<string?[]> GetActivationValues() => Task.FromResult(Array.Empty<string?>());
}

public sealed class JournalCompositionStorageProvider : IJournalStorageProvider
{
    private readonly ConcurrentDictionary<JournalId, JournalCompositionStorage> _storage = new();
    public IJournalStorage CreateStorage(JournalId journalId)
    {
        var result = _storage.GetOrAdd(journalId, static _ => new JournalCompositionStorage());
        Interlocked.Increment(ref result.Managers);
        return result;
    }
    public JournalCompositionStorage Get(GrainId id) => Get(JournalId.FromGrainId(id));
    public JournalCompositionStorage Get(JournalId id) => _storage[id];
    public bool Contains(JournalId id) => _storage.ContainsKey(id);
}

public sealed class JournalCompositionStorage : IJournalStorage
{
    public const string FailureMessage = "Expected journal commit acknowledgement failure.";
    private readonly VolatileJournalStorage _inner = new(JsonJournalExtensions.JournalFormatKey);
    public int Managers;
    public int Reads;
    public int Writes;
    public bool? FailNextWrite { get; set; }
    public bool IsCompactionRequested => false;
    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        return _inner.ReadAsync(consumer, cancellationToken);
    }
    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Writes);
        var failure = FailNextWrite;
        FailNextWrite = null;
        if (failure != false)
        {
            await _inner.AppendAsync(value, cancellationToken);
        }
        if (failure.HasValue)
        {
            throw new IOException(FailureMessage);
        }
    }
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => _inner.ReplaceAsync(value, cancellationToken);
    public ValueTask DeleteAsync(CancellationToken cancellationToken) => _inner.DeleteAsync(cancellationToken);
}
