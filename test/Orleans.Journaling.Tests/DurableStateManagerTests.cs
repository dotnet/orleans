using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class DurableStateManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_KeyedAndProgrammaticAccess_Converge(bool keyedFirst)
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        builder.Services.AddScoped<IGrainContext>(activationServices =>
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("registry-test", "activation"));
            context.ActivationServices.Returns(activationServices);
            return context;
        });
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var manager = scope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        Assert.Same(owner, manager);
        Assert.Same(scope.ServiceProvider, Assert.IsType<JournaledStateManager>(owner).ServiceProvider);

        var first = keyedFirst
            ? scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value")
            : manager.GetOrAddValue<int>("value");
        Assert.Same(first, manager.GetOrAddValue<int>("value"));
        Assert.Same(first, manager.GetOrAddState<IDurableValue<int>>("value"));
        Assert.Same(first, manager.GetOrAddState<object>("value"));
        Assert.Same(first, scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value"));
        AssertExisting(manager, "value", first);

        var lower = manager.GetOrAddValue<int>("state");
        var upper = manager.GetOrAddValue<int>("State");
        Assert.Same(lower, manager.GetOrAddValue<int>("state"));
        Assert.NotSame(lower, upper);
        AssertExisting(manager, "state", lower);
        AssertExisting(manager, "State", upper);
        AssertMissing<IDurableValue<int>>(manager, "absent");

        var explicitState = new InertState();
        owner.RegisterStateMachine("explicit", explicitState);
        Assert.Same(explicitState, manager.GetOrAddState<InertState>("explicit"));
        AssertExisting(manager, "explicit", explicitState);
        var duplicate = Assert.Throws<InvalidOperationException>(
            () => owner.RegisterStateMachine("explicit", new InertState()));
        Assert.Contains("explicit", duplicate.Message);
        AssertExisting(manager, "explicit", explicitState);
    }

    [Theory]
    [InlineData("closed-generic")]
    [InlineData("contract")]
    [InlineData("unsupported")]
    public async Task Registry_InvalidRequests_PreserveOriginalState(string requestKind)
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var manager = services.GetRequiredService<IJournaledStateManagerFactory>()
            .CreateStandalone(new JournalId("registry/invalid"));
        var original = manager.GetOrAddValue<int>("state");
        var owningServices = Assert.IsType<JournaledStateManager>(manager).ServiceProvider;

        switch (requestKind)
        {
            case "closed-generic":
                AssertIncompatible<IDurableValue<string>, DurableValue<string>>(manager, owningServices, original);
                break;
            case "contract":
                AssertIncompatible<IDurableList<int>, DurableList<int>>(manager, owningServices, original);
                break;
            case "unsupported":
                AssertMissing<IUnsupportedState>(manager, "missing");
                var error = Assert.Throws<InvalidOperationException>(
                    () => manager.GetOrAddState<IUnsupportedState>("missing"));
                Assert.Contains("No durable state implementation is registered", error.Message);
                Assert.Contains("missing", error.Message);
                Assert.Contains(typeof(IUnsupportedState).ToString(), error.Message);
                AssertMissing<IUnsupportedState>(manager, "missing");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(requestKind));
        }

        AssertExisting(manager, "state", original);
        Assert.Same(original, owningServices.GetRequiredKeyedService<IDurableValue<int>>("state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_CustomRegistration_ConstructsOnceAndOwnsScope(bool useFactory)
    {
        var builder = CreateBuilder();
        var constructions = new ProbeCounter();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        var calls = new List<(IServiceProvider Services, string Name)>();
        ProbeState CreateProbe(IServiceProvider owningServices, string name)
        {
            calls.Add((owningServices, name));
            return new ProbeState(owningServices.GetRequiredService<ScopedProbe>());
        }

        builder.AddJournaling();
        if (useFactory)
        {
            builder.Services.AddStateMachine<IProbeState, ProbeState>(CreateProbe);
        }
        else
        {
            builder.Services.AddStateMachine<IProbeState, ProbeState>();
        }

        builder.Services.AddSingleton<IJournalStorageProvider, VolatileJournalStorageProvider>();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        Assert.Null(services.GetService<IGrainContext>());
        await using var callerScope = services.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopedProbe>();
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        var idA = new JournalId("factory/scope-A");
        var idB = new JournalId("factory/scope-B");
        await using var managerA = factory.CreateStandalone(idA);
        await using var managerB = factory.CreateStandalone(idB);
        var scopeA = Assert.IsType<JournaledStateManager>(managerA).ServiceProvider;
        var scopeB = Assert.IsType<JournaledStateManager>(managerB).ServiceProvider;
        AssertManagerAliases(managerA, scopeA);
        AssertManagerAliases(managerB, scopeB);

        var probeA = Assert.IsType<ProbeState>(managerA.GetOrAddState<IProbeState>("probe"));
        var probeB = Assert.IsType<ProbeState>(scopeB.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.Same(probeA, managerA.GetOrAddState<IProbeState>("probe"));
        Assert.Same(probeA, scopeA.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.Same(probeB, managerB.GetOrAddState<IProbeState>("probe"));
        Assert.Same(probeB, scopeB.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.NotSame(probeA, probeB);
        Assert.Same(probeA.Marker, scopeA.GetRequiredService<ScopedProbe>());
        Assert.Same(probeB.Marker, scopeB.GetRequiredService<ScopedProbe>());
        Assert.NotSame(probeA.Marker, probeB.Marker);
        Assert.NotSame(callerMarker, probeA.Marker);
        Assert.NotSame(callerMarker, probeB.Marker);
        Assert.Equal(1, probeA.Marker.ConstructionCount);
        Assert.Equal(1, probeB.Marker.ConstructionCount);
        Assert.Equal(2, constructions.Count);
        if (useFactory)
        {
            Assert.Collection(calls,
                call => { Assert.Same(scopeA, call.Services); Assert.Equal("probe", call.Name); },
                call => { Assert.Same(scopeB, call.Services); Assert.Equal("probe", call.Name); });
        }
        else
        {
            Assert.Empty(calls);
        }

        var valueA = managerA.GetOrAddValue<int>("value");
        var valueB = managerB.GetOrAddValue<int>("value");
        Assert.NotSame(valueA, valueB);
        await WaitFor(managerA.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "initialize scope A");
        await WaitFor(managerB.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "initialize scope B");
        valueA.Value = 11;
        valueB.Value = 22;
        await WaitFor(managerA.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist scope A");
        await WaitFor(managerB.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist scope B");
        Assert.Equal(11, valueA.Value);
        Assert.Equal(22, valueB.Value);

        await managerA.DisposeAsync();
        await managerA.DisposeAsync();
        Assert.Equal(1, probeA.DisposalCount);
        Assert.Equal(0, probeB.DisposalCount);
        Assert.Equal(1, probeA.Marker.DisposalCount);
        Assert.Equal(0, probeB.Marker.DisposalCount);
        Assert.Equal(0, callerMarker.DisposalCount);
        valueB.Value = 23;
        await WaitFor(managerB.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist surviving scope B");
        Assert.Equal(23, valueB.Value);
        Assert.Same(factory, services.GetRequiredService<IJournaledStateManagerFactory>());
        await using (var freshCallerScope = services.CreateAsyncScope())
        {
            Assert.NotSame(callerMarker, freshCallerScope.ServiceProvider.GetRequiredService<ScopedProbe>());
        }

        await managerB.DisposeAsync();
        Assert.Equal(1, probeB.DisposalCount);
        Assert.Equal(1, probeA.DisposalCount);
        Assert.Equal(1, probeB.Marker.DisposalCount);
        Assert.Equal(1, probeA.Marker.DisposalCount);
        Assert.Equal(0, callerMarker.DisposalCount);
        Assert.Equal(2, constructions.Count);

        // Distinct live values alone would not catch two managers writing to the same journal.
        await using var recoveredA = factory.CreateStandalone(idA);
        await using var recoveredB = factory.CreateStandalone(idB);
        var persistedA = recoveredA.GetOrAddValue<int>("value");
        var persistedB = recoveredB.GetOrAddValue<int>("value");
        await WaitFor(recoveredA.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "recover scope A");
        await WaitFor(recoveredB.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "recover scope B");
        Assert.Equal(11, persistedA.Value);
        Assert.Equal(23, persistedB.Value);
    }

    [Fact]
    public async Task Factory_ManualState_ReplaysWithoutCreatingScope()
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var trackedServices = new TrackingServiceProvider(services);
        var factory = CreateStandaloneFactory(trackedServices);
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey);
        var id = new JournalId("factory/manual");

        await using (var manager = factory.CreateStandalone(id))
        {
            Assert.Equal(0, trackedServices.CreatedScopes);
            var value = new DurableValue<int>("value", manager, codec);
            Assert.Same(value, manager.GetOrAddValue<int>("value"));
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            value.Value = 42;
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, trackedServices.CreatedScopes);
        }

        await using (var manager = factory.CreateStandalone(id))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(42, value.Value);
            AssertExisting<IDurableValue<int>>(manager, "value", value);
            Assert.Same(value, manager.GetOrAddValue<int>("value"));
        }

        Assert.Equal(0, trackedServices.CreatedScopes);
        Assert.Equal(0, trackedServices.DisposedScopes);

        await using var unused = factory.CreateStandalone(new JournalId("factory/unused"));
        await unused.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => new JournalReplayContext(Assert.IsType<JournaledStateManager>(unused)).ServiceProvider);
        Assert.Equal(0, trackedServices.CreatedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_StateServices_CreateAndDisposeOneScope(bool replayServicesFirst)
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        builder.Services.AddSingleton<ProbeCounter>();
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddStateMachine<IProbeState, ProbeState>();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var trackedServices = new TrackingServiceProvider(services);
        var factory = CreateStandaloneFactory(trackedServices);
        await using var manager = factory.CreateStandalone(new JournalId("factory/lazy"));
        Assert.Equal(0, trackedServices.CreatedScopes);

        if (replayServicesFirst)
        {
            var replayServices = new JournalReplayContext(Assert.IsType<JournaledStateManager>(manager)).ServiceProvider;
            AssertManagerAliases(manager, replayServices);
            Assert.NotSame(services, replayServices);
        }

        var probe = Assert.IsType<ProbeState>(manager.GetOrAddState<IProbeState>("probe"));
        Assert.Equal(1, trackedServices.CreatedScopes);
        Assert.Same(probe, manager.GetOrAddState<IProbeState>("probe"));
        var value = manager.GetOrAddValue<int>("value");
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 17;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, trackedServices.CreatedScopes);
        Assert.Equal(0, trackedServices.DisposedScopes);

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(1, trackedServices.DisposedScopes);
        Assert.Equal(1, probe.DisposalCount);
        Assert.Equal(1, probe.Marker.DisposalCount);
        Assert.Throws<ObjectDisposedException>(() => new JournalReplayContext(Assert.IsType<JournaledStateManager>(manager)).ServiceProvider);
        Assert.Equal(1, trackedServices.CreatedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_InitializationBoundary_RejectsOnlyNewStates(bool duringRecovery)
    {
        var id = new JournalId("registry/initialization");
        var storage = new ControlledStorage();
        var readBarrier = new StorageBarrier();
        storage.NextReadBarrier = readBarrier;
        var constructions = new ProbeCounter();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        builder.AddJournaling();
        builder.Services.AddStateMachine<IProbeState, ProbeState>();
        builder.Services.AddSingleton<IJournalStorageProvider>(new SingleJournalProvider(id, storage));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        await using var manager = factory.CreateStandalone(id);
        var owningServices = Assert.IsType<JournaledStateManager>(manager).ServiceProvider;
        var value = manager.GetOrAddValue<int>("value");
        var probe = manager.GetOrAddState<IProbeState>("probe");
        var explicitState = new InertState();
        var initialization = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(readBarrier.Entered.Task, $"read started for {id}; duringRecovery={duringRecovery}");
            Assert.Equal(1, storage.ReadCount);
            Assert.False(initialization.IsCompleted);
            if (!duringRecovery)
            {
                readBarrier.Release();
                await WaitFor(initialization, $"read completed for {id}");
            }

            Assert.Same(value, manager.GetOrAddValue<int>("value"));
            Assert.Same(value, manager.GetOrAddState<IDurableValue<int>>("value"));
            Assert.Same(value, owningServices.GetRequiredKeyedService<IDurableValue<int>>("value"));
            AssertExisting(manager, "value", value);
            Assert.Same(probe, manager.GetOrAddState<IProbeState>("probe"));
            Assert.Same(probe, owningServices.GetRequiredKeyedService<IProbeState>("probe"));
            AssertExisting(manager, "probe", probe);

            AssertAdmissionRejected(() => manager.GetOrAddState<IProbeState>("late-programmatic"));
            AssertMissing<IProbeState>(manager, "late-programmatic");
            Assert.Equal(1, constructions.Count);
            AssertAdmissionRejected(() => owningServices.GetRequiredKeyedService<IProbeState>("late-keyed"));
            AssertMissing<IProbeState>(manager, "late-keyed");
            Assert.Equal(1, constructions.Count);
            AssertAdmissionRejected(() => manager.RegisterStateMachine("late-explicit", explicitState));
            AssertMissing<InertState>(manager, "late-explicit");
            AssertAdmissionRejected(() => manager.GetOrAddValue<int>("late-value"));
            AssertMissing<IDurableValue<int>>(manager, "late-value");
            AssertAdmissionRejected(() => owningServices.GetRequiredKeyedService<IDurableList<string>>("late-list"));
            AssertMissing<IDurableList<string>>(manager, "late-list");
            Assert.Equal(1, constructions.Count);
        }
        finally
        {
            readBarrier.Release();
        }

        await WaitFor(initialization, $"finish recovery for {id}");
        value.Value = 31;
        await WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"write after rejected registration for {id}");
        Assert.Equal(1, storage.SuccessfulAppendCount);
        await manager.DisposeAsync();

        await using var recovered = factory.CreateStandalone(id);
        var recoveredValue = recovered.GetOrAddValue<int>("value");
        recovered.GetOrAddState<IProbeState>("probe");
        await WaitFor(recovered.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fresh recovery for {id}");
        Assert.NotSame(value, recoveredValue);
        Assert.Equal(31, recoveredValue.Value);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(2, constructions.Count);
        AssertMissing<IProbeState>(recovered, "late-programmatic");
        AssertMissing<IProbeState>(recovered, "late-keyed");
        AssertMissing<InertState>(recovered, "late-explicit");
        AssertMissing<IDurableValue<int>>(recovered, "late-value");
        AssertMissing<IDurableList<string>>(recovered, "late-list");
    }

    [Fact]
    public async Task Factory_AllHelpers_ShareAcknowledgementAndRecover()
    {
        AssertNullManagerGuards();
        var id = new JournalId("factory/all-helpers");
        var storage = new ControlledStorage();
        var provider = new SingleJournalProvider(id, storage);
        var builder = CreateBuilder();
        builder.AddJournaling();
        builder.Services.AddSingleton<IJournalStorageProvider>(provider);
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        Assert.Null(services.GetService<IGrainContext>());
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        await using var manager = factory.CreateStandalone(id);
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey,
            Assert.IsType<JournaledStateManager>(manager).WriteJournalFormatKey);
        var states = new SevenStates(manager);
        await WaitFor(manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"initialize {id}");
        Assert.Equal(0, storage.AppendCount);
        Assert.False(states.Completion.Task.IsCompleted);
        states.Dictionary["a"] = 11;
        states.List.Add("first");
        states.List.Add("second");
        states.Queue.Enqueue(3);
        states.Queue.Enqueue(5);
        Assert.True(states.Set.Add("a"));
        Assert.True(states.Set.Add("b"));
        states.Value.Value = 17;
        Assert.True(states.Completion.TrySetResult(19));
        states.Persistent.State = "saved";
        var appendBarrier = new StorageBarrier();
        storage.NextAppendBarrier = appendBarrier;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(appendBarrier.Entered.Task, $"append entered for {id}");
            Assert.Equal(1, storage.AppendCount);
            Assert.Equal(0, storage.SuccessfulAppendCount);
            Assert.False(write.IsCompleted);
            Assert.False(states.Completion.Task.IsCompleted);
            Assert.False(states.Persistent.RecordExists);
            Assert.Equal("0", states.Persistent.Etag);
        }
        finally
        {
            appendBarrier.Release();
        }

        await WaitFor(write, $"shared acknowledgement for {id}");
        Assert.Equal(1, storage.SuccessfulAppendCount);
        await states.AssertPersisted();
        await manager.DisposeAsync();

        await using var recovered = factory.CreateStandalone(id);
        var recoveredStates = new SevenStates(recovered);
        Assert.NotSame(states.Dictionary, recoveredStates.Dictionary);
        Assert.NotSame(states.List, recoveredStates.List);
        Assert.NotSame(states.Queue, recoveredStates.Queue);
        Assert.NotSame(states.Set, recoveredStates.Set);
        Assert.NotSame(states.Value, recoveredStates.Value);
        Assert.NotSame(states.Completion, recoveredStates.Completion);
        Assert.NotSame(states.Persistent, recoveredStates.Persistent);
        await WaitFor(recovered.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"recover seven states for {id}");
        await recoveredStates.AssertPersisted();
        Assert.Equal(2, provider.CreateCount);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(1, storage.AppendCount);
        Assert.Equal(1, storage.SuccessfulAppendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_WriteFailure_FencesManagerAndRecoversCommit(bool committed)
    {
        var id = new JournalId("factory/failure");
        var storage = new ControlledStorage();
        var constructions = new ProbeCounter();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        builder.AddJournaling();
        builder.Services.AddStateMachine<IProbeState, ProbeState>();
        builder.Services.AddSingleton<IJournalStorageProvider>(new SingleJournalProvider(id, storage));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        await using var manager = factory.CreateStandalone(id);
        var value = manager.GetOrAddValue<int>("value");
        await WaitFor(manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"initialize {id}");
        value.Value = 1;
        await WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"persist initial value for {id}");
        value.Value = 2;
        var injected = new IOException("Injected append acknowledgement failure.");
        storage.NextAppendFailure = injected;
        storage.FailAfterCommit = committed;
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"failed append for {id}, committed={committed}"));
        Assert.Same(injected, failure);
        Assert.Equal(2, value.Value);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
        Assert.Equal(committed ? 2 : 1, storage.SuccessfulAppendCount);

        var writeRejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"fenced write for {id}"));
        AssertFenced(writeRejection, injected);
        var initializationRejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitFor(manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fenced recovery for {id}"));
        AssertFenced(initializationRejection, injected);
        var registrationRejection = Assert.Throws<InvalidOperationException>(
            () => manager.GetOrAddState<IProbeState>("late"));
        AssertFenced(registrationRejection, injected);
        Assert.Equal(0, constructions.Count);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
        Assert.Equal(committed ? 2 : 1, storage.SuccessfulAppendCount);
        await manager.DisposeAsync();

        await using var recovered = factory.CreateStandalone(id);
        var recoveredValue = recovered.GetOrAddValue<int>("value");
        await WaitFor(recovered.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fresh recovery for {id}, committed={committed}");
        Assert.NotSame(value, recoveredValue);
        Assert.Equal(committed ? 2 : 1, recoveredValue.Value);
        AssertMissing<IProbeState>(recovered, "late");
        Assert.Equal(0, constructions.Count);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
    }

    private static TestSiloBuilder CreateBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, TimeProvider.System);
        builder.Services.Configure<JournaledStateManagerOptions>(
            options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        return builder;
    }

    private static JournaledStateManagerFactory CreateStandaloneFactory(TrackingServiceProvider services)
    {
        var shared = new JournaledStateManagerShared(
            services.GetRequiredService<ILogger<JournaledStateManager>>(),
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>(),
            TimeProvider.System,
            services);
        return new(shared, services.GetRequiredService<IJournalStorageProvider>());
    }

    private static void AssertManagerAliases(IJournaledStateManager manager, IServiceProvider services)
    {
        Assert.Same(manager, services.GetRequiredService<IJournaledStateManager>());
        Assert.Same(manager, services.GetRequiredService<IDurableStateManager>());
    }

    private static void AssertExisting<TState>(IDurableStateManager manager, string name, TState expected) where TState : class
    {
        Assert.True(manager.TryGetState<TState>(name, out var actual));
        Assert.Same(expected, actual);
    }

    private static void AssertMissing<TState>(IDurableStateManager manager, string name) where TState : class
    {
        Assert.False(manager.TryGetState<TState>(name, out var state));
        Assert.Null(state);
    }

    private static void AssertIncompatible<TState, TImplementation>(
        IDurableStateManager manager, IServiceProvider services, IDurableValue<int> original)
        where TState : class
    {
        var getError = Assert.Throws<InvalidOperationException>(() => manager.GetOrAddState<TState>("state"));
        Assert.Contains("state", getError.Message);
        Assert.Contains(typeof(TState).ToString(), getError.Message);
        AssertExisting(manager, "state", original);
        var lookupError = Assert.Throws<InvalidOperationException>(() => manager.TryGetState<TState>("state", out _));
        Assert.Contains("state", lookupError.Message);
        Assert.Contains(typeof(TState).ToString(), lookupError.Message);
        AssertExisting(manager, "state", original);
        var keyedError = Assert.Throws<InvalidOperationException>(() => services.GetRequiredKeyedService<TState>("state"));
        Assert.Contains("state", keyedError.Message);
        // Built-ins self-register: this path diagnoses the conflicting implementation, not its interface.
        Assert.Contains(typeof(TImplementation).ToString(), keyedError.Message);
        AssertExisting(manager, "state", original);
    }

    private static void AssertAdmissionRejected(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("New states cannot be registered after journaled state manager initialization has begun.", error.Message);
    }

    private static void AssertFenced(InvalidOperationException error, IOException cause)
    {
        Assert.Contains("Journaled state operations are fenced", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    private static void AssertNullManagerGuards()
    {
        IDurableStateManager manager = null!;
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddDictionary<string, int>("dictionary")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddList<string>("list")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddQueue<int>("queue")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddSet<string>("set")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddValue<int>("value")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddTaskCompletionSource<int>("tcs")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddPersistentState<string>("persistent")).ParamName);
    }

    private static async Task WaitFor(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Timed out waiting for {phase}; task status: {task.Status}.", error);
        }
    }

    private sealed class SevenStates(IDurableStateManager manager)
    {
        public IDurableDictionary<string, int> Dictionary { get; } = manager.GetOrAddDictionary<string, int>("dictionary");
        public IDurableList<string> List { get; } = manager.GetOrAddList<string>("list");
        public IDurableQueue<int> Queue { get; } = manager.GetOrAddQueue<int>("queue");
        public IDurableSet<string> Set { get; } = manager.GetOrAddSet<string>("set");
        public IDurableValue<int> Value { get; } = manager.GetOrAddValue<int>("value");
        public IDurableTaskCompletionSource<int> Completion { get; } = manager.GetOrAddTaskCompletionSource<int>("tcs");
        public IPersistentState<string> Persistent { get; } = manager.GetOrAddPersistentState<string>("persistent");

        public async Task AssertPersisted()
        {
            var entry = Assert.Single(Dictionary);
            Assert.Equal("a", entry.Key);
            Assert.Equal(11, entry.Value);
            Assert.Equal(new[] { "first", "second" }, List);
            Assert.Equal(2, Queue.Count);
            Assert.Equal(new[] { 3, 5 }, Queue);
            Assert.Equal(3, Queue.Peek());
            Assert.Equal(2, Set.Count);
            Assert.True(Set.SetEquals(["a", "b"]));
            Assert.Equal(17, Value.Value);
            Assert.Equal(DurableTaskCompletionSourceStatus.Completed, Completion.State.Status);
            Assert.Equal(19, Completion.State.Value);
            await WaitFor(Completion.Task, "durable TCS acknowledgement");
            Assert.Equal(19, await Completion.Task);
            Assert.Equal("saved", Persistent.State);
            Assert.True(Persistent.RecordExists);
            Assert.Equal("1", Persistent.Etag);
        }
    }

    public interface IUnsupportedState { }
    public interface IProbeState { }

    public class InertState : IStateMachine
    {
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context)
            => throw new InvalidOperationException("The inert probe does not write replayable entries.");
        public void Reset(JournalStreamWriter writer) { }
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) { }
    }

    public sealed class ProbeState : InertState, IProbeState, IAsyncDisposable
    {
        private int _disposalCount;

        public ProbeState(ScopedProbe marker)
        {
            Marker = marker;
            marker.OnConstructed();
        }

        public ScopedProbe Marker { get; }
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposalCount);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ProbeCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    public sealed class ScopedProbe(ProbeCounter counter) : IAsyncDisposable
    {
        private int _constructionCount;
        private int _disposalCount;
        public int ConstructionCount => Volatile.Read(ref _constructionCount);
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public void OnConstructed()
        {
            Interlocked.Increment(ref _constructionCount);
            counter.Increment();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposalCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingServiceProvider(ServiceProvider inner) : IKeyedServiceProvider, IServiceScopeFactory
    {
        public int CreatedScopes { get; private set; }
        public int DisposedScopes { get; private set; }

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public object? GetKeyedService(Type serviceType, object? serviceKey)
            => inner.GetKeyedService(serviceType, serviceKey);

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => inner.GetRequiredKeyedService(serviceType, serviceKey);

        public IServiceScope CreateScope()
        {
            var scope = inner.CreateAsyncScope();
            CreatedScopes++;
            return new TrackedScope(this, scope);
        }

        private sealed class TrackedScope(TrackingServiceProvider owner, AsyncServiceScope scope) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;

            public void Dispose()
            {
                scope.Dispose();
                owner.DisposedScopes++;
            }

            public async ValueTask DisposeAsync()
            {
                await scope.DisposeAsync();
                owner.DisposedScopes++;
            }
        }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class SingleJournalProvider(JournalId id, IJournalStorage storage) : IJournalStorageProvider
    {
        public int CreateCount { get; private set; }

        public IJournalStorage CreateStorage(JournalId journalId)
        {
            Assert.Equal(id, journalId);
            CreateCount++;
            return storage;
        }
    }

    private sealed class StorageBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Released.TrySetResult();

        public async Task Enter(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ControlledStorage : IJournalStorage
    {
        private readonly VolatileJournalStorage _inner = new(OrleansBinaryJournalFormat.JournalFormatKey);
        private int _readCount;
        private int _appendCount;
        private int _successfulAppendCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int AppendCount => Volatile.Read(ref _appendCount);
        public int SuccessfulAppendCount => Volatile.Read(ref _successfulAppendCount);
        public StorageBarrier? NextReadBarrier { get; set; }
        public StorageBarrier? NextAppendBarrier { get; set; }
        public IOException? NextAppendFailure { get; set; }
        public bool FailAfterCommit { get; set; }
        public bool IsCompactionRequested => _inner.IsCompactionRequested;

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            if (NextReadBarrier is { } barrier)
            {
                NextReadBarrier = null;
                await barrier.Enter(cancellationToken);
            }

            await _inner.ReadAsync(consumer, cancellationToken);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appendCount);
            if (NextAppendBarrier is { } barrier)
            {
                NextAppendBarrier = null;
                await barrier.Enter(cancellationToken);
            }

            var failure = NextAppendFailure;
            NextAppendFailure = null;
            if (failure is not null && !FailAfterCommit)
                throw failure;

            await _inner.AppendAsync(value, cancellationToken);
            Interlocked.Increment(ref _successfulAppendCount);
            if (failure is not null)
                throw failure;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            => _inner.ReplaceAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
            => _inner.DeleteAsync(cancellationToken);
    }
}
